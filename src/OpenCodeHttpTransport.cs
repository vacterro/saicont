using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SaiCont
{
    internal enum OpenCodeStatusKind { Unknown, Idle, Busy, Retry, Error }

    internal sealed class OpenCodeSseEvent
    {
        public string Id;
        public string Directory;
        public string Project;
        public string Type;
        public string SessionId;
        public OpenCodeStatusKind Status;
        public DateTime RetryAtUtc;
        public string Text;
        public string FailureCode;
        public string Agent;
        public string Provider;
        public string Model;
    }

    // OpenCode 1.18.x HTTP transport. SSE is the live signal; REST is used
    // only for identity/reconciliation. No TUI endpoint is used.
    internal sealed class OpenCodeHttpTransport : ITargetTransport
    {
        private const int RequestTimeoutMs = 5000;
        private const int SendTimeoutMs = 30000;
        private const int MaxBodyBytes = 1024 * 1024;
        private const int MaxTextChars = 16000;
        private const string SupportedVersionPrefix = "1.18.";
        private const int MinReconnectDelayMs = 500;
        private const int MaxReconnectDelayMs = 60000;

        private readonly object _gate = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Dictionary<string, OpenCodeRuntimeSession> _sessions = new Dictionary<string, OpenCodeRuntimeSession>(StringComparer.Ordinal);
        private readonly HashSet<string> _seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenEventOrder = new Queue<string>();
        private readonly string _endpoint;
        private readonly string _projectDirectory;
        private readonly Thread _eventThread;
        private volatile bool _stopping;
        private volatile bool _connected;
        private bool _malformedEvent;
        private bool _reconcileRequired;
        private string _boundSessionId;
        private SendOperation _send;
        private HttpWebRequest _activeRequest;
        private HttpWebRequest _activeSendRequest;
        private readonly bool _restoredSessionAmbiguous;

        public OpenCodeHttpTransport() : this(null, null) { }

        internal OpenCodeHttpTransport(string endpoint, string projectDirectory)
            : this(endpoint, projectDirectory, null, false) { }

        internal OpenCodeHttpTransport(string endpoint, string projectDirectory, string initialSessionId, bool restoredSessionAmbiguous)
        {
            _endpoint = endpoint;
            _projectDirectory = projectDirectory;
            _boundSessionId = initialSessionId;
            _restoredSessionAmbiguous = restoredSessionAmbiguous;
            _malformedEvent = restoredSessionAmbiguous;
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "SAICONT OpenCode SSE" };
            if (!String.IsNullOrWhiteSpace(endpoint) && !String.IsNullOrWhiteSpace(projectDirectory)) _eventThread.Start();
        }

        public string Kind { get { return "opencode-http"; } }
        public bool HasPendingSend { get { lock (_gate) { return _send != null && !_send.Completed; } } }

        public bool IsApplicable(TargetRule rule)
        {
            return rule != null && String.Equals(rule.Transport ?? "console", Kind, StringComparison.OrdinalIgnoreCase)
                && !String.IsNullOrWhiteSpace(rule.Endpoint) && !String.IsNullOrWhiteSpace(rule.ProjectDirectory);
        }

        public TargetObservation Poll(TargetRule rule, DateTime nowUtc, out string error)
        {
            error = null;
            var observation = new TargetObservation
            {
                TargetName = rule == null ? null : rule.Name,
                TransportKind = Kind,
                EndpointIdentity = rule == null ? null : rule.Endpoint,
                ProjectIdentity = rule == null ? null : rule.ProjectDirectory,
                TriggerToken = null,
                StartRow = 0,
                CanSend = false,
                RetryAtUtc = DateTime.MinValue
            };
            if (!IsApplicable(rule))
            {
                error = "transport_not_applicable_or_project_unpinned";
                observation.BlockReason = error;
                return observation;
            }

            HealthPayload health;
            if (!TryGetHealth(rule.Endpoint, out health, out error))
            {
                observation.BlockReason = "server_unavailable: " + error;
                return observation;
            }
            if (health == null || String.IsNullOrEmpty(health.Version) || !health.Version.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal))
            {
                error = "unknown_opencode_version: " + (health == null ? "missing" : health.Version);
                observation.BlockReason = error;
                return observation;
            }
            observation.EndpointIdentity = CanonicalEndpoint(rule.Endpoint) + "|" + health.Version;

            IList<SessionSummary> sessions;
            if (!TryGetSessions(rule, out sessions, out error))
            {
                observation.BlockReason = "session_list_unavailable: " + error;
                return observation;
            }
            SessionSummary session;
            string resolveError;
            if (!TryResolveBoundSession(sessions, out session, out resolveError))
            {
                Dictionary<string, OpenCodeStatusKind> coldStatuses;
                if (!TryGetStatuses(rule, out coldStatuses, out error))
                {
                    observation.BlockReason = "status_unavailable: " + error;
                    return observation;
                }
                List<SessionSummary> busy = FindBusySessions(sessions, coldStatuses);
                if (busy.Count != 1)
                {
                    error = busy.Count == 0 ? "no_active_session" : "ambiguous_active_sessions";
                    observation.BlockReason = error;
                    return observation;
                }
                BindSession(busy[0].Id);
                session = busy[0];
            }
            if (session == null)
            {
                observation.BlockReason = resolveError ?? "session_unresolved";
                return observation;
            }

            observation.SessionIdentity = session.Id;
            observation.ActiveAgent = session.Agent;
            observation.ModelProvider = session.Provider;
            observation.ModelId = session.Model;
            rule.ActiveAgentForSend = session.Agent;
            rule.ModelProviderForSend = session.Provider;
            rule.ModelIdForSend = session.Model;

            Dictionary<string, OpenCodeStatusKind> statuses;
            if (!TryGetStatuses(rule, out statuses, out error))
            {
                observation.BlockReason = "status_unavailable: " + error;
                return observation;
            }
            MessageReconciliation messages;
            if (!TryGetMessages(rule, session.Id, out messages, out error))
            {
                observation.BlockReason = "messages_unavailable: " + error;
                return observation;
            }

            OpenCodeStatusKind sseStatus;
            string sseText;
            string sseFailure;
            DateTime retryAt;
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(session.Id);
                sseStatus = runtime.Status;
                sseText = runtime.Text;
                sseFailure = runtime.FailureCode;
                retryAt = runtime.RetryAtUtc;
                observation.TriggerToken = runtime.LastEventId;
            }
            OpenCodeStatusKind apiStatus;
            statuses.TryGetValue(session.Id, out apiStatus);
            if (sseStatus != OpenCodeStatusKind.Unknown && apiStatus != OpenCodeStatusKind.Unknown && sseStatus != apiStatus)
            {
                observation.BlockReason = "conflicting_state: sse=" + sseStatus + " api=" + apiStatus;
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }
            if (_malformedEvent)
            {
                observation.BlockReason = "malformed_sse_event_requires_reconciliation";
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }
            if (!_connected)
            {
                observation.BlockReason = "sse_disconnected_without_live_reconciliation";
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }
            if (_reconcileRequired)
            {
                observation.BlockReason = "sse_reconnect_reconciliation_pending";
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }
            if (sseStatus == OpenCodeStatusKind.Unknown)
            {
                observation.BlockReason = "sse_state_unknown";
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }

            OpenCodeStatusKind effective = sseStatus != OpenCodeStatusKind.Unknown ? sseStatus : apiStatus;
            bool busyState = effective == OpenCodeStatusKind.Busy || effective == OpenCodeStatusKind.Retry;
            bool errorState = effective == OpenCodeStatusKind.Error;
            string combined = JoinText(sseText, messages.Text);
            if (!String.IsNullOrEmpty(sseFailure)) combined = JoinText(combined, sseFailure);
            // Keep the legacy rule matcher useful for trigger/failure policy,
            // but make the API-owned lifecycle state visible as the current
            // line. Readiness is overridden below from the normalized state;
            // this marker only prevents a configured ready regex from having
            // to understand provider-specific message text.
            combined = JoinText(combined, "[opencode status=" + effective.ToString().ToLowerInvariant() + "]");
            observation.Text = Limit(combined, MaxTextChars);
            observation.CursorRow = CountNewLines(observation.Text);
            observation.Busy = busyState;
            observation.Ready = !busyState && effective == OpenCodeStatusKind.Idle;
            observation.RetryAtUtc = retryAt;
            observation.FailureCode = sseFailure ?? messages.FailureCode;
            observation.CanSend = !errorState && observation.Ready && !HasPendingSend;
            if (!observation.CanSend) observation.BlockReason = HasPendingSend ? "send_pending" : (errorState ? "session_error" : "session_not_ready");
            return observation;
        }

        public bool ValidatePreSend(TargetObservation observation, TargetRule rule, out string error)
        {
            error = null;
            if (observation == null || String.IsNullOrEmpty(observation.SessionIdentity)) { error = "missing_session_identity"; return false; }
            if (!IsApplicable(rule) || !String.Equals(rule.Endpoint, _endpoint, StringComparison.OrdinalIgnoreCase)) { error = "transport_identity_changed"; return false; }
            if (_stopping) { error = "transport_disposed"; return false; }
            if (HasPendingSend) { error = "send_pending"; return false; }
            HealthPayload health;
            if (!TryGetHealth(rule.Endpoint, out health, out error) || health == null || String.IsNullOrEmpty(health.Version) || !health.Version.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal)) { error = error ?? "unknown_opencode_version"; return false; }
            if (!String.Equals(observation.EndpointIdentity, CanonicalEndpoint(rule.Endpoint) + "|" + health.Version, StringComparison.OrdinalIgnoreCase)) { error = "runtime_generation_changed"; return false; }
            SessionSummary fresh;
            if (!TryGetSession(rule, observation.SessionIdentity, out fresh, out error)) return false;
            if (!String.Equals(NormalizePath(fresh.Directory), NormalizePath(rule.ProjectDirectory), StringComparison.OrdinalIgnoreCase)) { error = "project_mismatch"; return false; }
            if (String.IsNullOrWhiteSpace(fresh.Agent) || String.IsNullOrWhiteSpace(fresh.Provider) || String.IsNullOrWhiteSpace(fresh.Model)) { error = "model_or_agent_context_missing"; return false; }
            if (!String.Equals(fresh.Agent, observation.ActiveAgent, StringComparison.Ordinal) || !String.Equals(fresh.Provider, observation.ModelProvider, StringComparison.Ordinal) || !String.Equals(fresh.Model, observation.ModelId, StringComparison.Ordinal)) { error = "model_or_agent_changed"; return false; }
            Dictionary<string, OpenCodeStatusKind> statuses;
            if (!TryGetStatuses(rule, out statuses, out error)) return false;
            OpenCodeStatusKind apiStatus;
            if (statuses.TryGetValue(fresh.Id, out apiStatus) && (apiStatus == OpenCodeStatusKind.Busy || apiStatus == OpenCodeStatusKind.Retry)) { error = "session_busy_on_recheck"; return false; }
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(fresh.Id);
                if (runtime.Status == OpenCodeStatusKind.Busy || runtime.Status == OpenCodeStatusKind.Retry) { error = "sse_busy_on_recheck"; return false; }
                if (runtime.Status == OpenCodeStatusKind.Error || _malformedEvent || !_connected || _reconcileRequired) { error = "live_signal_not_send_safe"; return false; }
            }
            return true;
        }

        public SendOutcome Send(TargetObservation observation, TargetRule rule, string command, Func<bool> commitAllowed, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(command) || command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0) { error = "invalid_command"; return new SendOutcome { Disposition = SendDisposition.AmbiguousOrPartialInput }; }
            if (!ValidatePreSend(observation, rule, out error)) return new SendOutcome { Disposition = SendDisposition.NoInputCommitted };
            if (commitAllowed == null || !commitAllowed()) { error = "authorization_revoked_before_http_commit"; return new SendOutcome { Disposition = SendDisposition.NoInputCommitted }; }
            lock (_gate)
            {
                if (_send != null && !_send.Completed) { error = "send_pending"; return new SendOutcome { Disposition = SendDisposition.NoInputCommitted }; }
                _send = new SendOperation
                {
                    SessionId = observation.SessionIdentity,
                    EndpointIdentity = observation.EndpointIdentity,
                    Command = command,
                    Rule = rule,
                    Agent = observation.ActiveAgent,
                    Provider = observation.ModelProvider,
                    Model = observation.ModelId,
                    CommitAllowed = commitAllowed
                };
                ThreadPool.QueueUserWorkItem(CompleteSend, _send);
            }
            error = "http_message_pending";
            return new SendOutcome { Disposition = SendDisposition.Pending, IdentityChecked = observation.SessionIdentity };
        }

        public bool TryTakeCompletedSend(string sessionIdentity, out SendOutcome outcome)
        {
            outcome = null;
            lock (_gate)
            {
                if (_send == null || !_send.Completed || !String.Equals(_send.SessionId, sessionIdentity, StringComparison.Ordinal)) return false;
                outcome = _send.Outcome;
                _send = null;
                return true;
            }
        }

        public void Dispose()
        {
            _stopping = true;
            HttpWebRequest request;
            HttpWebRequest sendRequest;
            lock (_gate) { request = _activeRequest; sendRequest = _activeSendRequest; }
            if (request != null) { try { request.Abort(); } catch { } }
            if (sendRequest != null) { try { sendRequest.Abort(); } catch { } }
            if (_eventThread != null && _eventThread.IsAlive) _eventThread.Join(1000);
        }

        internal static bool TryParseSseFrame(string frame, out OpenCodeSseEvent parsed, out string error)
        {
            parsed = null;
            error = null;
            if (String.IsNullOrWhiteSpace(frame)) { error = "empty_sse_frame"; return false; }
            var data = new StringBuilder();
            string[] lines = frame.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines) if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line.Substring(5).TrimStart()); }
            if (data.Length == 0) { error = "sse_frame_has_no_data"; return false; }
            try
            {
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(data.ToString()) as Dictionary<string, object>;
                if (root == null) { error = "sse_json_not_object"; return false; }
                Dictionary<string, object> payload = GetMap(root, "payload") ?? root;
                string type = GetString(payload, "type");
                if (String.IsNullOrEmpty(type)) { error = "sse_event_type_missing"; return false; }
                Dictionary<string, object> properties = GetMap(payload, "properties") ?? new Dictionary<string, object>();
                var result = new OpenCodeSseEvent { Id = GetString(payload, "id"), Directory = GetString(root, "directory"), Project = GetString(root, "project"), Type = type, SessionId = GetString(properties, "sessionID") };
                if (type == "session.status")
                {
                    Dictionary<string, object> status = GetMap(properties, "status");
                    result.Status = ParseStatus(GetString(status, "type"));
                    result.RetryAtUtc = UnixTime(GetLong(status, "next"));
                    result.Text = GetString(status, "message");
                }
                else if (type == "session.idle") result.Status = OpenCodeStatusKind.Idle;
                else if (type == "session.error")
                {
                    result.Status = OpenCodeStatusKind.Error;
                    result.FailureCode = ExtractFailureCode(GetMap(properties, "error"), out result.Text);
                }
                else if (type == "message.updated")
                {
                    Dictionary<string, object> info = GetMap(properties, "info");
                    result.SessionId = result.SessionId ?? GetString(info, "sessionID");
                    result.Agent = GetString(info, "agent"); result.Provider = GetString(info, "providerID"); result.Model = GetString(info, "modelID");
                    Dictionary<string, object> errorMap = GetMap(info, "error");
                    if (errorMap != null) result.FailureCode = ExtractFailureCode(errorMap, out result.Text);
                }
                else if (type == "message.part.delta") result.Text = GetString(properties, "delta");
                else if (type == "message.part.updated") result.Text = GetString(GetMap(properties, "part"), "text");
                else if (type == "session.updated")
                {
                    Dictionary<string, object> info = GetMap(properties, "info");
                    result.SessionId = result.SessionId ?? GetString(info, "id"); result.Agent = GetString(info, "agent");
                    Dictionary<string, object> model = GetMap(info, "model"); result.Provider = GetString(model, "providerID"); result.Model = GetString(model, "id");
                }
                parsed = result;
                return true;
            }
            catch (Exception ex) { error = "malformed_sse_json: " + ex.Message; return false; }
        }

        internal static IList<OpenCodeSseEvent> ParseSseFrames(string text, out string error)
        {
            error = null; var result = new List<OpenCodeSseEvent>();
            if (text == null) return result;
            string[] frames = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { "\n\n" }, StringSplitOptions.None);
            foreach (string frame in frames)
            {
                if (String.IsNullOrWhiteSpace(frame)) continue;
                OpenCodeSseEvent item; string itemError;
                if (!TryParseSseFrame(frame, out item, out itemError)) { error = itemError; return result; }
                result.Add(item);
            }
            return result;
        }

        private void EventLoop()
        {
            // T-158: exponential reconnect backoff. A flat 500ms retry means a
            // dead/never-started endpoint is dialed twice a second forever,
            // which is what produced the endless
            // "WebException:Unable to connect to the remote server" stream.
            // Successful connect resets the ladder immediately.
            int reconnectDelayMs = MinReconnectDelayMs;
            while (!_stopping)
            {
                bool connectedThisRound = false;
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BuildUri("/event", true));
                    request.Method = "GET"; request.Accept = "text/event-stream"; request.Timeout = RequestTimeoutMs;
                    // /event is a long-lived stream. A finite read timeout
                    // would manufacture a disconnect every few seconds even
                    // while OpenCode is healthy; REST calls remain bounded.
                    request.ReadWriteTimeout = Timeout.Infinite; request.KeepAlive = true;
                    lock (_gate) { _activeRequest = request; }
                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        _connected = true; _reconcileRequired = false;
                        connectedThisRound = true;
                        reconnectDelayMs = MinReconnectDelayMs;
                        var frame = new StringBuilder();
                        while (!_stopping)
                        {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            if (line.Length == 0) { ProcessFrame(frame.ToString()); frame.Length = 0; }
                            else if (frame.Length < MaxBodyBytes) frame.Append(line).Append('\n');
                            else { _malformedEvent = true; frame.Length = 0; }
                        }
                        ProcessFrame(frame.ToString());
                    }
                }
                catch { _connected = false; _reconcileRequired = true; }
                finally { lock (_gate) { _activeRequest = null; } _connected = false; _reconcileRequired = true; }
                int waitMs = reconnectDelayMs;
                if (!connectedThisRound)
                {
                    reconnectDelayMs = Math.Min(MaxReconnectDelayMs, reconnectDelayMs * 2);
                }
                // Stay responsive to Dispose(): sleep in 50ms slices.
                for (int i = 0; i < waitMs && !_stopping; i += 50) Thread.Sleep(50);
            }
        }

        private void ProcessFrame(string frame)
        {
            if (String.IsNullOrWhiteSpace(frame)) return;
            OpenCodeSseEvent item; string error;
            if (!TryParseSseFrame(frame, out item, out error)) { _malformedEvent = true; _reconcileRequired = true; return; }
            lock (_gate)
            {
                if (!String.IsNullOrEmpty(item.Id))
                {
                    if (_seenEventIds.Contains(item.Id)) return;
                    _seenEventIds.Add(item.Id); _seenEventOrder.Enqueue(item.Id);
                    while (_seenEventOrder.Count > 512) _seenEventIds.Remove(_seenEventOrder.Dequeue());
                }
                if (String.IsNullOrEmpty(item.SessionId)) return;
                if (!String.IsNullOrEmpty(item.Directory) && !String.Equals(NormalizePath(item.Directory), NormalizePath(_projectDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    _malformedEvent = true;
                    _reconcileRequired = true;
                    return;
                }
                OpenCodeRuntimeSession state = GetRuntimeSessionLocked(item.SessionId);
                // A message delta is progress, not a new failure occurrence.
                // Keep the trigger identity anchored to the error/retry event;
                // otherwise every streamed assistant part would make the same
                // stale error look like a fresh retry opportunity.
                if (!String.IsNullOrEmpty(item.Id) &&
                    (item.Type == "session.error" || item.Status == OpenCodeStatusKind.Retry || !String.IsNullOrEmpty(item.FailureCode)))
                {
                    state.LastEventId = item.Id;
                }
                if (item.Status != OpenCodeStatusKind.Unknown) state.Status = item.Status;
                if (item.RetryAtUtc != DateTime.MinValue) state.RetryAtUtc = item.RetryAtUtc;
                if (!String.IsNullOrEmpty(item.Text)) state.Text = Limit(JoinText(state.Text, item.Text), MaxTextChars);
                if (!String.IsNullOrEmpty(item.FailureCode)) { state.FailureCode = item.FailureCode; state.Status = OpenCodeStatusKind.Error; BindSessionLocked(item.SessionId); }
                if (item.Type == "session.status" && (item.Status == OpenCodeStatusKind.Busy || item.Status == OpenCodeStatusKind.Retry)) BindSessionLocked(item.SessionId);
            }
        }

        private void CompleteSend(object value)
        {
            SendOperation operation = (SendOperation)value;
            SendOutcome outcome; string error = null;
            if (operation.CommitAllowed == null || !operation.CommitAllowed())
            {
                outcome = new SendOutcome { Disposition = SendDisposition.NoInputCommitted, IdentityChecked = operation.SessionId }; error = "authorization_revoked_before_http_commit";
            }
            else if (!ValidateCommitIdentity(operation, out error))
            {
                outcome = new SendOutcome { Disposition = SendDisposition.NoInputCommitted, IdentityChecked = operation.SessionId };
            }
            else
            {
                int status; string response;
                string body = BuildMessageBody(operation.Rule, operation.Command);
                if (!TryHttp("POST", operation.Rule.Endpoint, "/session/" + Uri.EscapeDataString(operation.SessionId) + "/message", operation.Rule.ProjectDirectory, body, SendTimeoutMs, out status, out response, out error)) outcome = new SendOutcome { Disposition = SendDisposition.AmbiguousOrPartialInput, IdentityChecked = operation.SessionId };
                else if (status == 200 && IsConfirmedMessage(response, operation.SessionId, operation.Rule)) { outcome = new SendOutcome { Disposition = SendDisposition.CompleteInputCommitted, ConfirmationToken = ExtractId(response, "msg_"), IdentityChecked = operation.SessionId }; error = null; }
                else if (status >= 200 && status < 300) { outcome = new SendOutcome { Disposition = SendDisposition.AmbiguousOrPartialInput, IdentityChecked = operation.SessionId }; error = "http_success_without_execution_confirmation: status=" + status; }
                else { outcome = new SendOutcome { Disposition = SendDisposition.AmbiguousOrPartialInput, IdentityChecked = operation.SessionId }; error = "send_rejected: status=" + status + " body=" + Limit(response, 300); }
            }
            lock (_gate) { operation.Outcome = outcome; operation.Error = error; operation.Completed = true; }
        }

        // This is deliberately repeated on the worker immediately before the
        // POST. The poll-time check proves the candidate; this check protects
        // the final commit window from session replacement, model changes,
        // stale SSE, and a newly-busy session.
        private bool ValidateCommitIdentity(SendOperation operation, out string error)
        {
            error = null;
            if (operation == null || operation.Rule == null) { error = "send_operation_missing"; return false; }
            if (_stopping) { error = "transport_disposed_before_commit"; return false; }
            if (operation.CommitAllowed == null || !operation.CommitAllowed()) { error = "authorization_revoked_before_http_commit"; return false; }
            HealthPayload health;
            if (!TryGetHealth(operation.Rule.Endpoint, out health, out error) || health == null || String.IsNullOrEmpty(health.Version) || !health.Version.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal)) { error = error ?? "unknown_opencode_version"; return false; }
            if (!String.Equals(operation.EndpointIdentity, CanonicalEndpoint(operation.Rule.Endpoint) + "|" + health.Version, StringComparison.OrdinalIgnoreCase)) { error = "runtime_generation_changed_before_commit"; return false; }
            SessionSummary fresh;
            if (!TryGetSession(operation.Rule, operation.SessionId, out fresh, out error)) return false;
            if (!String.Equals(NormalizePath(fresh.Directory), NormalizePath(operation.Rule.ProjectDirectory), StringComparison.OrdinalIgnoreCase)) { error = "project_mismatch_before_commit"; return false; }
            if (!String.Equals(fresh.Agent, operation.Agent, StringComparison.Ordinal) || !String.Equals(fresh.Provider, operation.Provider, StringComparison.Ordinal) || !String.Equals(fresh.Model, operation.Model, StringComparison.Ordinal)) { error = "model_or_agent_changed_before_commit"; return false; }
            Dictionary<string, OpenCodeStatusKind> statuses;
            if (!TryGetStatuses(operation.Rule, out statuses, out error)) return false;
            OpenCodeStatusKind apiStatus;
            if (statuses.TryGetValue(operation.SessionId, out apiStatus) && apiStatus != OpenCodeStatusKind.Unknown && apiStatus != OpenCodeStatusKind.Idle) { error = "session_not_idle_before_commit"; return false; }
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(operation.SessionId);
                if (runtime.Status != OpenCodeStatusKind.Idle || _malformedEvent || !_connected || _reconcileRequired) { error = "live_signal_not_send_safe_before_commit"; return false; }
            }
            return !_stopping && (operation.CommitAllowed == null || operation.CommitAllowed());
        }

        private bool TryResolveBoundSession(IList<SessionSummary> sessions, out SessionSummary session, out string error)
        {
            session = null; error = null;
            lock (_gate)
            {
                if (String.IsNullOrEmpty(_boundSessionId)) { error = "no_bound_session"; return false; }
                foreach (SessionSummary candidate in sessions) if (String.Equals(candidate.Id, _boundSessionId, StringComparison.Ordinal)) { session = candidate; return true; }
            }
            error = "stale_session_disappeared"; return false;
        }
        private void BindSession(string id) { lock (_gate) { BindSessionLocked(id); } }
        private void BindSessionLocked(string id) { if (String.IsNullOrEmpty(_boundSessionId)) _boundSessionId = id; else if (!String.Equals(_boundSessionId, id, StringComparison.Ordinal)) _malformedEvent = true; }

        private bool TryGetSessions(TargetRule rule, out IList<SessionSummary> sessions, out string error)
        {
            int status; string body; sessions = null;
            if (!TryHttp("GET", rule.Endpoint, "/session", rule.ProjectDirectory, null, RequestTimeoutMs, out status, out body, out error) || status < 200 || status >= 300) { error = error ?? "session_list_status=" + status; return false; }
            try
            {
                object[] array = _json.DeserializeObject(body) as object[]; if (array == null) { error = "session_list_not_array"; return false; }
                var list = new List<SessionSummary>();
                foreach (object item in array)
                {
                    SessionSummary parsed = ParseSession(item as Dictionary<string, object>);
                    if (parsed == null || String.IsNullOrEmpty(parsed.Id)) { error = "session_identity_missing"; return false; }
                    if (String.Equals(NormalizePath(parsed.Directory), NormalizePath(rule.ProjectDirectory), StringComparison.OrdinalIgnoreCase)) list.Add(parsed);
                }
                sessions = list; return true;
            }
            catch (Exception ex) { error = "malformed_session_json: " + ex.Message; return false; }
        }

        private bool TryGetSession(TargetRule rule, string sessionId, out SessionSummary session, out string error)
        {
            int status; string body; session = null;
            if (!TryHttp("GET", rule.Endpoint, "/session/" + Uri.EscapeDataString(sessionId), rule.ProjectDirectory, null, RequestTimeoutMs, out status, out body, out error) || status < 200 || status >= 300) { error = error ?? "session_status=" + status; return false; }
            try { session = ParseSession(_json.DeserializeObject(body) as Dictionary<string, object>); if (session == null || !String.Equals(session.Id, sessionId, StringComparison.Ordinal)) { error = "session_identity_mismatch"; return false; } return true; }
            catch (Exception ex) { error = "malformed_session_json: " + ex.Message; return false; }
        }

        private bool TryGetStatuses(TargetRule rule, out Dictionary<string, OpenCodeStatusKind> statuses, out string error)
        {
            int status; string body; statuses = new Dictionary<string, OpenCodeStatusKind>(StringComparer.Ordinal);
            if (!TryHttp("GET", rule.Endpoint, "/session/status", rule.ProjectDirectory, null, RequestTimeoutMs, out status, out body, out error) || status < 200 || status >= 300) { error = error ?? "status_status=" + status; return false; }
            try
            {
                Dictionary<string, object> map = _json.DeserializeObject(body) as Dictionary<string, object>; if (map == null) { error = "status_not_object"; return false; }
                foreach (KeyValuePair<string, object> pair in map) { OpenCodeStatusKind kind = ParseStatus(GetString(pair.Value as Dictionary<string, object>, "type")); if (kind == OpenCodeStatusKind.Unknown) { error = "unknown_session_status"; return false; } statuses[pair.Key] = kind; }
                return true;
            }
            catch (Exception ex) { error = "malformed_status_json: " + ex.Message; return false; }
        }

        private bool TryGetMessages(TargetRule rule, string sessionId, out MessageReconciliation messages, out string error)
        {
            int status; string body; messages = new MessageReconciliation();
            if (!TryHttp("GET", rule.Endpoint, "/session/" + Uri.EscapeDataString(sessionId) + "/message?limit=20", rule.ProjectDirectory, null, RequestTimeoutMs, out status, out body, out error) || status < 200 || status >= 300) { error = error ?? "message_status=" + status; return false; }
            try
            {
                object[] array = _json.DeserializeObject(body) as object[]; if (array == null) { error = "message_list_not_array"; return false; }
                var text = new StringBuilder();
                foreach (object item in array)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>; Dictionary<string, object> info = GetMap(map, "info"); if (info == null) { error = "message_info_missing"; return false; }
                    if (String.Equals(GetString(info, "role"), "assistant", StringComparison.OrdinalIgnoreCase)) { Dictionary<string, object> errorMap = GetMap(info, "error"); if (errorMap != null) { string detail; messages.FailureCode = ExtractFailureCode(errorMap, out detail); text.Append(' ').Append(detail); } }
                    object[] parts = GetArray(map, "parts"); if (parts != null) foreach (object partValue in parts) { string partText = GetString(partValue as Dictionary<string, object>, "text"); if (!String.IsNullOrEmpty(partText)) text.Append(' ').Append(partText); }
                }
                messages.Text = Limit(text.ToString(), MaxTextChars); return true;
            }
            catch (Exception ex) { error = "malformed_message_json: " + ex.Message; return false; }
        }

        private static List<SessionSummary> FindBusySessions(IList<SessionSummary> sessions, Dictionary<string, OpenCodeStatusKind> statuses)
        {
            var result = new List<SessionSummary>(); foreach (SessionSummary session in sessions) { OpenCodeStatusKind kind; if (statuses.TryGetValue(session.Id, out kind) && (kind == OpenCodeStatusKind.Busy || kind == OpenCodeStatusKind.Retry)) result.Add(session); } return result;
        }
        private static SessionSummary ParseSession(Dictionary<string, object> map)
        {
            if (map == null) return null; var result = new SessionSummary { Id = GetString(map, "id"), Directory = GetString(map, "directory"), Agent = GetString(map, "agent") }; Dictionary<string, object> model = GetMap(map, "model"); result.Provider = GetString(model, "providerID"); result.Model = GetString(model, "id"); return result;
        }
        private OpenCodeRuntimeSession GetRuntimeSessionLocked(string id) { OpenCodeRuntimeSession state; if (!_sessions.TryGetValue(id, out state)) { state = new OpenCodeRuntimeSession(); _sessions[id] = state; } return state; }
        private Uri BuildUri(string path, bool projectQuery) { string value = (_endpoint ?? String.Empty).TrimEnd('/') + path; if (projectQuery) value += "?directory=" + Uri.EscapeDataString(_projectDirectory ?? String.Empty); return new Uri(value, UriKind.Absolute); }

        private bool TryHttp(string method, string endpoint, string path, string projectDirectory, string body, int timeoutMs, out int status, out string responseBody, out string error)
        {
            status = 0; responseBody = String.Empty; error = null;
            bool isSend = String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path.IndexOf("/message", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                string url = endpoint.TrimEnd('/') + path; if (!String.IsNullOrEmpty(projectDirectory)) url += (path.IndexOf('?') >= 0 ? "&" : "?") + "directory=" + Uri.EscapeDataString(projectDirectory);
                var request = (HttpWebRequest)WebRequest.Create(new Uri(url, UriKind.Absolute)); request.Method = method; request.Accept = "application/json"; request.ContentType = "application/json"; request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs; request.KeepAlive = false;
                if (isSend) lock (_gate) { _activeSendRequest = request; }
                if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length; using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
                using (WebResponse response = request.GetResponse()) { HttpWebResponse http = response as HttpWebResponse; status = http == null ? 200 : (int)http.StatusCode; responseBody = ReadBounded(response.GetResponseStream(), MaxBodyBytes); return true; }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null) { status = (int)response.StatusCode; try { responseBody = ReadBounded(response.GetResponseStream(), MaxBodyBytes); } catch { } error = "http_status=" + status; return true; }
                error = ex.GetType().Name + ":" + ex.Message; return false;
            }
            catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; return false; }
            finally { if (isSend) lock (_gate) { _activeSendRequest = null; } }
        }
        private bool TryGetHealth(string endpoint, out HealthPayload health, out string error)
        {
            int status; string body; health = null;
            if (!TryHttp("GET", endpoint, "/global/health", null, null, RequestTimeoutMs, out status, out body, out error) || status < 200 || status >= 300) { error = error ?? "health_status=" + status; return false; }
            try { Dictionary<string, object> map = _json.DeserializeObject(body) as Dictionary<string, object>; health = new HealthPayload { Healthy = GetBool(map, "healthy"), Version = GetString(map, "version") }; if (!health.Healthy) { error = "server_unhealthy"; return false; } return true; }
            catch (Exception ex) { error = "malformed_health_json: " + ex.Message; return false; }
        }

        private static string BuildMessageBody(TargetRule rule, string command)
        {
            var body = new Dictionary<string, object>(); body["agent"] = rule.ActiveAgentForSend; body["model"] = new Dictionary<string, object> { { "providerID", rule.ModelProviderForSend }, { "modelID", rule.ModelIdForSend } }; body["parts"] = new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", command } } }; return new JavaScriptSerializer().Serialize(body);
        }
        private bool IsConfirmedMessage(string body, string sessionId, TargetRule rule)
        {
            try
            {
                Dictionary<string, object> root = _json.DeserializeObject(body ?? String.Empty) as Dictionary<string, object>;
                Dictionary<string, object> info = GetMap(root, "info") ?? root;
                return info != null &&
                    String.Equals(GetString(info, "role"), "assistant", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(GetString(info, "sessionID"), sessionId, StringComparison.Ordinal) &&
                    String.Equals(GetString(info, "modelID"), rule.ModelIdForSend, StringComparison.Ordinal) &&
                    String.Equals(GetString(info, "providerID"), rule.ModelProviderForSend, StringComparison.Ordinal);
            }
            catch { return false; }
        }
        private static string ExtractId(string body, string prefix) { int start = body == null ? -1 : body.IndexOf("\"id\":\"" + prefix, StringComparison.Ordinal); if (start < 0) return null; start += 6; int end = body.IndexOf('"', start); return end > start ? body.Substring(start, end - start) : null; }
        private static string ExtractFailureCode(Dictionary<string, object> map, out string text)
        {
            text = String.Empty; if (map == null) return null; Dictionary<string, object> data = GetMap(map, "data"); string message = GetString(data, "message") ?? GetString(map, "message") ?? String.Empty; text = message; int code = message.IndexOf("\"code\":\"", StringComparison.OrdinalIgnoreCase); if (code >= 0) { int start = code + 8; int end = message.IndexOf('"', start); if (end > start) return message.Substring(start, end - start); } string name = GetString(map, "name"); return String.Equals(name, "APIError", StringComparison.OrdinalIgnoreCase) ? "api_error" : name;
        }
        private static OpenCodeStatusKind ParseStatus(string value) { if (String.Equals(value, "idle", StringComparison.OrdinalIgnoreCase)) return OpenCodeStatusKind.Idle; if (String.Equals(value, "busy", StringComparison.OrdinalIgnoreCase)) return OpenCodeStatusKind.Busy; if (String.Equals(value, "retry", StringComparison.OrdinalIgnoreCase)) return OpenCodeStatusKind.Retry; return OpenCodeStatusKind.Unknown; }
        private static DateTime UnixTime(long value) { if (value <= 0) return DateTime.MinValue; try { return DateTimeOffset.FromUnixTimeMilliseconds(value > 100000000000L ? value : value * 1000L).UtcDateTime; } catch { return DateTime.MinValue; } }
        private static Dictionary<string, object> GetMap(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        private static object[] GetArray(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) ? value as object[] : null; }
        private static string GetString(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        private static long GetLong(Dictionary<string, object> map, string key) { object value; long result; return map != null && map.TryGetValue(key, out value) && Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0; }
        private static bool GetBool(Dictionary<string, object> map, string key) { object value; bool result; return map != null && map.TryGetValue(key, out value) && Boolean.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) && result; }
        private static string JoinText(string first, string second) { return String.IsNullOrEmpty(second) ? first ?? String.Empty : (String.IsNullOrEmpty(first) ? second : first + "\n" + second); }
        private static string Limit(string value, int max) { if (String.IsNullOrEmpty(value)) return String.Empty; return value.Length <= max ? value : value.Substring(value.Length - max); }
        private static int CountNewLines(string value) { int count = 0; if (value != null) foreach (char c in value) if (c == '\n') count++; return count; }
        private static string NormalizePath(string value) { return String.IsNullOrEmpty(value) ? String.Empty : value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(); }
        private static string CanonicalEndpoint(string value) { return (value ?? String.Empty).TrimEnd('/').ToLowerInvariant(); }
        private static string ReadBounded(Stream stream, int maxBytes) { using (stream) { var ms = new MemoryStream(); byte[] buffer = new byte[8192]; int total = 0, read; while (total < maxBytes && (read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - total))) > 0) { ms.Write(buffer, 0, read); total += read; } return Encoding.UTF8.GetString(ms.ToArray()); } }

        private sealed class HealthPayload { public bool Healthy; public string Version; }
        private sealed class SessionSummary { public string Id; public string Directory; public string Agent; public string Provider; public string Model; }
        private sealed class OpenCodeRuntimeSession { public OpenCodeStatusKind Status; public DateTime RetryAtUtc; public string Text; public string FailureCode; public string LastEventId; }
        private sealed class MessageReconciliation { public string Text; public string FailureCode; }
        private sealed class SendOperation { public string SessionId; public string EndpointIdentity; public string Command; public TargetRule Rule; public string Agent; public string Provider; public string Model; public Func<bool> CommitAllowed; public volatile bool Completed; public SendOutcome Outcome; public string Error; }
    }
}
