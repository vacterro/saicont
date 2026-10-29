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

    // PERF-001 / CORE-002 / CORE-004 test seam: replaces the real REST layer with
    // a deterministic in-process responder so request counts and reconciliation
    // transitions can be asserted without a live endpoint or a network.
    internal delegate bool OpenCodeHttpInvoke(string method, string path, string body, out int status, out string responseBody, out string error);

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
        private readonly Queue<string> _sessionOrder = new Queue<string>();
        private readonly HashSet<string> _seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenEventOrder = new Queue<string>();
        private readonly string _endpoint;
        private readonly string _projectDirectory;
        private readonly Thread _eventThread;
        private volatile bool _stopping;
        private volatile bool _connected;
        // CORE-002: ambiguity is a RESOLVABLE state, not a one-way poison latch.
        // _ambiguityGeneration names the SSE stream generation whose evidence is
        // untrustworthy; it is cleared only after a NEWER stream is established
        // AND authoritative REST/SSE identity agree, never merely because the
        // socket reopened. _streamGeneration increments on every connect.
        private int _streamGeneration;
        private int _ambiguousGeneration = -1;
        private string _ambiguityReason;
        // CORE-002: reconciliation requires a NEWER stream generation, and a
        // malformed frame does not disconnect by itself, so ambiguity has to ASK
        // for a fresh stream. One request per generation: the event loop observes
        // the flag, ends the round, and reconnects with generation + 1.
        private volatile bool _ambiguityDisconnectRequested;
        private int _ambiguityDisconnectGeneration = -1;
        private bool _reconcileRequired;
        private string _boundSessionId;
        private SendOperation _send;
        private HttpWebRequest _activeRequest;
        private readonly HashSet<HttpWebRequest> _activeRestRequests = new HashSet<HttpWebRequest>();
        private HttpWebRequest _activeSendRequest;
        private readonly bool _restoredSessionAmbiguous;

        // PERF-002: bound the runtime-session table. The bound session and any
        // session carrying pending send / failure authority are never evicted;
        // everything else is a recent-LRU window.
        private const int MaxRetainedSessions = 64;
        // PERF-002: hard bound on ONE physical SSE input line. StreamReader
        // would otherwise materialize an arbitrarily long line before the frame
        // guard could reject it.
        private const int MaxLineChars = 256 * 1024;

        // PERF-001: one authoritative REST snapshot per poll generation.
        private ObservationSnapshot _snapshot;
        private long _snapshotSequence;
        private const int SnapshotLifetimeMs = 750;
        // PERF-001: single-use proof that a candidate already passed
        // ValidatePreSend inside the current snapshot generation.
        private long _validationTicketSequence = -1;
        private string _validationTicketSession;
        private string _validationTicketEndpointIdentity;
        // CORE-004: monotonic per-transport send generation.
        private long _sendGeneration;
        // PERF-001: last message-history evidence, keyed by the SSE evidence
        // sequence that produced it.
        private string _messageCacheSession;
        private int _messageCacheStreamGeneration = -1;
        private long _messageCacheEvidenceSequence = -1;
        private bool _messageCacheValid;
        private MessageReconciliation _messageCache;

        public OpenCodeHttpTransport() : this(null, null) { }

        internal OpenCodeHttpTransport(string endpoint, string projectDirectory)
            : this(endpoint, projectDirectory, null, false) { }

        internal OpenCodeHttpTransport(string endpoint, string projectDirectory, string initialSessionId, bool restoredSessionAmbiguous)
            : this(endpoint, projectDirectory, initialSessionId, restoredSessionAmbiguous, true) { }

        // PERF-001 test seam: startEventLoop=false keeps the SSE thread from
        // dialing anything, so a test transport performs exactly the REST calls
        // the harness counts and nothing else.
        internal OpenCodeHttpTransport(string endpoint, string projectDirectory, string initialSessionId, bool restoredSessionAmbiguous, bool startEventLoop)
        {
            _endpoint = endpoint;
            _projectDirectory = projectDirectory;
            _boundSessionId = initialSessionId;
            _restoredSessionAmbiguous = restoredSessionAmbiguous;
            if (restoredSessionAmbiguous)
            {
                // Generation 0 is the first stream this transport will open, so
                // marking generation 0 ambiguous means "no stream observed so
                // far is trustworthy"; reconciliation requires generation >= 1.
                _ambiguousGeneration = 0;
                _ambiguityReason = "restored_session_ambiguous";
            }
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "SAICONT OpenCode SSE" };
            if (startEventLoop && !String.IsNullOrWhiteSpace(endpoint) && !String.IsNullOrWhiteSpace(projectDirectory)) _eventThread.Start();
        }

        internal static OpenCodeHttpTransport CreateForTests(string endpoint, string projectDirectory, string initialSessionId, bool restoredSessionAmbiguous)
        {
            return new OpenCodeHttpTransport(endpoint, projectDirectory, initialSessionId, restoredSessionAmbiguous, false);
        }

        public string Kind { get { return "opencode-http"; } }
        public bool HasPendingSend { get { lock (_gate) { return _send != null && !_send.Completed; } } }

        /// <summary>
        /// PERF-001 test seam: when set, every REST call is served by this
        /// delegate instead of HttpWebRequest. Null in production.
        /// </summary>
        internal OpenCodeHttpInvoke HttpInvokeForTests;

        /// <summary>
        /// CORE-002 test seam: force the live-stream state a running EventLoop
        /// would otherwise own, so reconciliation can be exercised deterministically
        /// without a socket. Advancing the generation models a NEW stream.
        /// </summary>
        internal void SetStreamStateForTests(bool connected, bool advanceGeneration)
        {
            lock (_gate)
            {
                if (advanceGeneration) _streamGeneration++;
                _connected = connected;
                _reconcileRequired = !connected;
            }
        }

        /// <summary>CORE-002 test seam: inject SSE-derived session state.</summary>
        internal void SetSessionStateForTests(string sessionId, OpenCodeStatusKind status, string text, string failureCode)
        {
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(sessionId);
                runtime.Status = status;
                runtime.FailureCode = failureCode;
                if (!String.IsNullOrEmpty(text)) runtime.AppendText(text, MaxTextChars);
                runtime.EvidenceSequence++;
            }
        }

        /// <summary>CORE-002 test seam: bind the session the transport tracks.</summary>
        internal void BindSessionForTests(string sessionId)
        {
            lock (_gate) { _boundSessionId = sessionId; }
        }

        /// <summary>CORE-002 test seam: mark evidence ambiguous.</summary>
        internal void MarkAmbiguousForTests(string reason)
        {
            MarkAmbiguous(reason);
        }

        /// <summary>PERF-002 test seam: feed one SSE frame through the real path.</summary>
        internal void ProcessFrameForTests(string frame)
        {
            ProcessFrame(frame);
        }

        /// <summary>PERF-002 test seam: the retained text for one session.</summary>
        internal string SessionTextForTests(string sessionId)
        {
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime;
                return _sessions.TryGetValue(sessionId, out runtime) ? runtime.TextSnapshot() : null;
            }
        }

        // CORE-002: true while the current evidence is untrustworthy. Reading it
        // does not clear it; only ClearAmbiguityIfReconciledLocked can.
        internal bool IsAmbiguous { get { lock (_gate) { return _ambiguousGeneration >= 0; } } }
        internal string AmbiguityReason { get { lock (_gate) { return _ambiguityReason; } } }

        /// <summary>CORE-002 test seam: whether a fresh stream has been requested.</summary>
        internal bool AmbiguityDisconnectRequestedForTests { get { return _ambiguityDisconnectRequested; } }

        private void MarkAmbiguousLocked(string reason)
        {
            _ambiguousGeneration = _streamGeneration;
            _ambiguityReason = reason;
            _reconcileRequired = true;
            // CORE-002: ask the event loop for a fresh stream ONCE per poisoned
            // generation. Without this a malformed frame on a healthy socket would
            // wait forever for a newer generation that nothing was going to open.
            if (_ambiguityDisconnectGeneration != _streamGeneration)
            {
                _ambiguityDisconnectGeneration = _streamGeneration;
                _ambiguityDisconnectRequested = true;
            }
        }

        private void MarkAmbiguous(string reason)
        {
            HttpWebRequest streamRequest = null;
            lock (_gate)
            {
                MarkAmbiguousLocked(reason);
                if (_ambiguityDisconnectRequested) streamRequest = _activeRequest;
            }
            // CORE-002: the event thread may be parked inside ReadLine on a
            // perfectly healthy socket. Abort the stream so it ends this round and
            // reconnects with a newer generation; the loop's own flag check covers
            // the case where this call came from the event thread itself.
            if (streamRequest != null) { try { streamRequest.Abort(); } catch { } }
        }

        /// <summary>
        /// CORE-002: the only path out of ambiguity. Requires a strictly NEWER
        /// live stream generation than the one that produced the ambiguity, plus
        /// authoritative agreement supplied by the caller (fresh REST session in
        /// this project, exactly one bound session, REST and SSE both idle).
        /// A reconnect alone advances the generation but never satisfies this.
        /// </summary>
        private bool ClearAmbiguityIfReconciledLocked(string sessionId, bool authoritativeAgreement)
        {
            if (_ambiguousGeneration < 0) return true;
            if (!authoritativeAgreement) return false;
            if (!_connected || _reconcileRequired) return false;
            if (_streamGeneration <= _ambiguousGeneration) return false;
            if (String.IsNullOrEmpty(_boundSessionId) || !String.Equals(_boundSessionId, sessionId, StringComparison.Ordinal)) return false;
            _ambiguousGeneration = -1;
            _ambiguityReason = null;
            return true;
        }

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
            // W2L1-004: never begin a REST sequence on a transport that is being
            // torn down. Without this the worker could enter four sequential
            // 5-second requests after shutdown already started.
            if (_stopping)
            {
                error = "transport_disposed";
                observation.BlockReason = error;
                return observation;
            }

            // PERF-001: one bounded authoritative snapshot per poll generation.
            // Several rules may share this transport within a single PollOnce;
            // they observe one identity fact, not one per rule.
            ObservationSnapshot snapshot = BeginSnapshot(nowUtc);

            HealthPayload health;
            if (!TryGetHealthCached(snapshot, rule.Endpoint, out health, out error))
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
            if (!TryGetSessionsCached(snapshot, rule, out sessions, out error))
            {
                observation.BlockReason = "session_list_unavailable: " + error;
                return observation;
            }
            Dictionary<string, OpenCodeStatusKind> statuses;
            if (!TryGetStatusesCached(snapshot, rule, out statuses, out error))
            {
                observation.BlockReason = "status_unavailable: " + error;
                return observation;
            }
            SessionSummary session;
            string resolveError;
            if (!TryResolveBoundSession(sessions, out session, out resolveError))
            {
                // PERF-001: the status map above is the same fact the cold path
                // used to fetch a second time.
                List<SessionSummary> busy = FindBusySessions(sessions, statuses);
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

            OpenCodeStatusKind sseStatus;
            string sseText;
            string sseFailure;
            DateTime retryAt;
            long evidenceSeq;
            int streamGeneration;
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(session.Id);
                sseStatus = runtime.Status;
                sseText = runtime.TextSnapshot();
                sseFailure = runtime.FailureCode;
                retryAt = runtime.RetryAtUtc;
                evidenceSeq = runtime.EvidenceSequence;
                streamGeneration = _streamGeneration;
                observation.TriggerToken = runtime.LastEventId;
            }
            MessageReconciliation messages;
            if (!TryGetMessagesCached(snapshot, rule, session.Id, streamGeneration, evidenceSeq, out messages, out error))
            {
                observation.BlockReason = "messages_unavailable: " + error;
                return observation;
            }

            OpenCodeStatusKind apiStatus;
            statuses.TryGetValue(session.Id, out apiStatus);
            if (sseStatus != OpenCodeStatusKind.Unknown && apiStatus != OpenCodeStatusKind.Unknown && sseStatus != apiStatus)
            {
                observation.BlockReason = "conflicting_state: sse=" + sseStatus + " api=" + apiStatus;
                observation.Diagnostic = observation.BlockReason;
                return observation;
            }
            // CORE-002: attempt reconciliation with the authoritative evidence
            // just gathered. Agreement means: this session is present in THIS
            // project's REST session list, it is the single bound session, and
            // REST plus SSE both report it idle on a live stream that is newer
            // than the one whose evidence was poisoned.
            lock (_gate)
            {
                if (_ambiguousGeneration >= 0)
                {
                    bool agreement = _connected && !_reconcileRequired &&
                        apiStatus == OpenCodeStatusKind.Idle &&
                        sseStatus == OpenCodeStatusKind.Idle &&
                        !ClearedByForeignProject(session, rule);
                    if (!ClearAmbiguityIfReconciledLocked(session.Id, agreement))
                    {
                        observation.BlockReason = "malformed_sse_event_requires_reconciliation";
                        observation.Diagnostic = observation.BlockReason + ": " + (_ambiguityReason ?? "unknown");
                        return observation;
                    }
                }
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
            ObservationSnapshot snapshot = CurrentSnapshotOrNew();
            HealthPayload health;
            if (!TryGetHealthCached(snapshot, rule.Endpoint, out health, out error) || health == null || String.IsNullOrEmpty(health.Version) || !health.Version.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal)) { error = error ?? "unknown_opencode_version"; return false; }
            if (!String.Equals(observation.EndpointIdentity, CanonicalEndpoint(rule.Endpoint) + "|" + health.Version, StringComparison.OrdinalIgnoreCase)) { error = "runtime_generation_changed"; return false; }
            SessionSummary fresh;
            if (!TryGetSessionCached(snapshot, rule, observation.SessionIdentity, out fresh, out error)) return false;
            if (!String.Equals(NormalizePath(fresh.Directory), NormalizePath(rule.ProjectDirectory), StringComparison.OrdinalIgnoreCase)) { error = "project_mismatch"; return false; }
            if (String.IsNullOrWhiteSpace(fresh.Agent) || String.IsNullOrWhiteSpace(fresh.Provider) || String.IsNullOrWhiteSpace(fresh.Model)) { error = "model_or_agent_context_missing"; return false; }
            if (!String.Equals(fresh.Agent, observation.ActiveAgent, StringComparison.Ordinal) || !String.Equals(fresh.Provider, observation.ModelProvider, StringComparison.Ordinal) || !String.Equals(fresh.Model, observation.ModelId, StringComparison.Ordinal)) { error = "model_or_agent_changed"; return false; }
            Dictionary<string, OpenCodeStatusKind> statuses;
            if (!TryGetStatusesCached(snapshot, rule, out statuses, out error)) return false;
            OpenCodeStatusKind apiStatus;
            if (statuses.TryGetValue(fresh.Id, out apiStatus) && (apiStatus == OpenCodeStatusKind.Busy || apiStatus == OpenCodeStatusKind.Retry)) { error = "session_busy_on_recheck"; return false; }
            lock (_gate)
            {
                OpenCodeRuntimeSession runtime = GetRuntimeSessionLocked(fresh.Id);
                if (runtime.Status == OpenCodeStatusKind.Busy || runtime.Status == OpenCodeStatusKind.Retry) { error = "sse_busy_on_recheck"; return false; }
                if (runtime.Status == OpenCodeStatusKind.Error || _ambiguousGeneration >= 0 || !_connected || _reconcileRequired) { error = "live_signal_not_send_safe"; return false; }
                // PERF-001: record that this exact candidate passed validation in
                // this snapshot generation, so Send does not repeat the same three
                // REST calls. The ticket is single-use and dies with the snapshot;
                // the independent pre-POST ValidateCommitIdentity is untouched.
                _validationTicketSequence = snapshot.Sequence;
                _validationTicketSession = fresh.Id;
                _validationTicketEndpointIdentity = observation.EndpointIdentity;
            }
            return true;
        }

        public SendOutcome Send(TargetObservation observation, TargetRule rule, string command, Func<bool> commitAllowed, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(command) || command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0) { error = "invalid_command"; return new SendOutcome { Disposition = SendDisposition.AmbiguousOrPartialInput }; }
            if (!TryConsumeValidationTicket(observation) && !ValidatePreSend(observation, rule, out error)) return new SendOutcome { Disposition = SendDisposition.NoInputCommitted };
            if (commitAllowed == null || !commitAllowed()) { error = "authorization_revoked_before_http_commit"; return new SendOutcome { Disposition = SendDisposition.NoInputCommitted }; }
            lock (_gate)
            {
                if (_send != null && !_send.Completed) { error = "send_pending"; return new SendOutcome { Disposition = SendDisposition.NoInputCommitted }; }
                _send = new SendOperation
                {
                    // CORE-004: the owner identity of an asynchronous send. Session
                    // identity ALONE is not ownership: two rules can legally share
                    // endpoint+project+session, and the completion would then be
                    // consumable by whichever rule polled next. Rule name plus
                    // state key plus a per-transport monotonic generation makes
                    // consumption exact.
                    OwnerRule = rule == null ? null : rule.Name,
                    OwnerStateKey = observation.OwnerStateKey,
                    OperationGeneration = ++_sendGeneration,
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

        /// <summary>
        /// CORE-004: completion belongs to the operation's owner and to nobody
        /// else. The owner is (rule, state key, session, generation); a caller
        /// that cannot name all four cannot consume the result. Sharing one
        /// transport for connection/observation is fine, but send ownership is
        /// per-rule.
        /// </summary>
        public bool TryTakeCompletedSend(SendOwner owner, out SendOutcome outcome)
        {
            outcome = null;
            if (owner == null) return false;
            lock (_gate)
            {
                if (_send == null || !_send.Completed) return false;
                if (!String.Equals(_send.SessionId, owner.SessionIdentity, StringComparison.Ordinal)) return false;
                if (!String.Equals(_send.OwnerRule, owner.RuleName, StringComparison.Ordinal)) return false;
                if (!String.Equals(_send.OwnerStateKey ?? String.Empty, owner.StateKey ?? String.Empty, StringComparison.Ordinal)) return false;
                outcome = _send.Outcome;
                _send = null;
                return true;
            }
        }

        public void CancelInFlight()
        {
            // W2L1-004: abort the outstanding REST/SSE requests so a poll worker
            // blocked inside them can return. The transport stays usable; only
            // the current requests die. Dispose() is the terminal operation.
            AbortActiveRequests();
        }

        public void Dispose()
        {
            _stopping = true;
            AbortActiveRequests();
            if (_eventThread != null && _eventThread.IsAlive) _eventThread.Join(1000);
        }

        private void AbortActiveRequests()
        {
            List<HttpWebRequest> requests;
            lock (_gate)
            {
                requests = new List<HttpWebRequest>(_activeRestRequests);
                if (_activeRequest != null) requests.Add(_activeRequest);
                if (_activeSendRequest != null) requests.Add(_activeSendRequest);
            }
            foreach (HttpWebRequest request in requests) { try { request.Abort(); } catch { } }
        }

        private bool TryConsumeValidationTicket(TargetObservation observation)
        {
            if (observation == null || String.IsNullOrEmpty(observation.SessionIdentity)) return false;
            lock (_gate)
            {
                if (_validationTicketSequence < 0) return false;
                ObservationSnapshot snapshot = _snapshot;
                if (snapshot == null || snapshot.Sequence != _validationTicketSequence) { InvalidateValidationTicketLocked(); return false; }
                if (!String.Equals(_validationTicketSession, observation.SessionIdentity, StringComparison.Ordinal)) return false;
                if (!String.Equals(_validationTicketEndpointIdentity, observation.EndpointIdentity, StringComparison.OrdinalIgnoreCase)) return false;
                if (_stopping || _ambiguousGeneration >= 0 || !_connected || _reconcileRequired) { InvalidateValidationTicketLocked(); return false; }
                if (_send != null && !_send.Completed) return false;
                InvalidateValidationTicketLocked();
                return true;
            }
        }

        private void InvalidateValidationTicketLocked()
        {
            _validationTicketSequence = -1;
            _validationTicketSession = null;
            _validationTicketEndpointIdentity = null;
        }

        internal static bool TryParseSseFrame(string frame, out OpenCodeSseEvent parsed, out string error)
        {
            return TryParseSseFrame(frame, null, out parsed, out error);
        }

        // PERF-002: accept an existing serializer so the single event thread
        // stops constructing one per frame. A null serializer keeps the old
        // allocate-per-call behaviour for the static test/utility entry point.
        internal static bool TryParseSseFrame(string frame, JavaScriptSerializer serializer, out OpenCodeSseEvent parsed, out string error)
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
                JavaScriptSerializer json = serializer ?? new JavaScriptSerializer();
                Dictionary<string, object> root = json.DeserializeObject(data.ToString()) as Dictionary<string, object>;
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
                HttpWebRequest sseRequest = null;
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BuildUri("/event", true));
                    request.Method = "GET"; request.Accept = "text/event-stream"; request.Timeout = RequestTimeoutMs;
                    // /event is a long-lived stream. A finite read timeout
                    // would manufacture a disconnect every few seconds even
                    // while OpenCode is healthy; REST calls remain bounded.
                    request.ReadWriteTimeout = Timeout.Infinite; request.KeepAlive = true;
                    sseRequest = request;
                    lock (_gate) { _activeRequest = request; }
                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        lock (_gate)
                        {
                            // CORE-002: a NEW stream generation. This alone is not
                            // reconciliation -- Poll must still see authoritative
                            // REST/SSE agreement before ambiguity clears -- but it
                            // is the precondition, so record it.
                            _streamGeneration++;
                            _connected = true;
                            _reconcileRequired = false;
                            _ambiguityDisconnectRequested = false;
                        }
                        connectedThisRound = true;
                        reconnectDelayMs = MinReconnectDelayMs;
                        var frame = new StringBuilder();
                        while (!_stopping && !_ambiguityDisconnectRequested)
                        {
                            string line;
                            bool lineOverflow;
                            if (!TryReadBoundedLine(reader, out line, out lineOverflow)) break;
                            if (lineOverflow)
                            {
                                // PERF-002: the input line itself exceeded the
                                // bound, so it was discarded WHILE reading rather
                                // than materialized in full. Fail closed.
                                MarkAmbiguous("oversized_sse_line");
                                frame.Length = 0;
                                continue;
                            }
                            if (line.Length == 0) { ProcessFrame(frame.ToString()); frame.Length = 0; }
                            else if (frame.Length < MaxBodyBytes) frame.Append(line).Append('\n');
                            else { MarkAmbiguous("oversized_sse_frame"); frame.Length = 0; }
                        }
                        ProcessFrame(frame.ToString());
                    }
                }
                catch { _connected = false; _reconcileRequired = true; }
                finally
                {
                    // Clear only this stream's registration. REST requests keep
                    // separate identities so neither completion loses the other.
                    lock (_gate) { if (sseRequest != null && ReferenceEquals(_activeRequest, sseRequest)) _activeRequest = null; }
                    _connected = false; _reconcileRequired = true;
                }
                int waitMs = reconnectDelayMs;
                if (!connectedThisRound)
                {
                    reconnectDelayMs = Math.Min(MaxReconnectDelayMs, reconnectDelayMs * 2);
                }
                // Stay responsive to Dispose(): sleep in 50ms slices.
                for (int i = 0; i < waitMs && !_stopping; i += 50) Thread.Sleep(50);
            }
        }

        /// <summary>
        /// PERF-002: read one SSE line with a hard character bound enforced
        /// WHILE consuming input. StreamReader.ReadLine() would allocate the
        /// whole line first, so a hostile//broken producer could force an
        /// arbitrary allocation before any frame guard ran. Returns false at
        /// end of stream; sets <paramref name="overflow"/> when the line was
        /// too long, in which case the remainder is drained and discarded.
        /// </summary>
        private static bool TryReadBoundedLine(StreamReader reader, out string line, out bool overflow)
        {
            line = null;
            overflow = false;
            int first = reader.Read();
            if (first < 0) return false;
            var builder = new StringBuilder(128);
            int current = first;
            while (current >= 0)
            {
                char c = (char)current;
                if (c == '\n') break;
                if (c == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    break;
                }
                if (builder.Length >= MaxLineChars)
                {
                    overflow = true;
                    // Drain to the end of this line without retaining it.
                    while ((current = reader.Read()) >= 0)
                    {
                        if (current == '\n') break;
                        if (current == '\r') { if (reader.Peek() == '\n') reader.Read(); break; }
                    }
                    line = String.Empty;
                    return true;
                }
                builder.Append(c);
                current = reader.Read();
            }
            line = builder.ToString();
            return true;
        }

        private void ProcessFrame(string frame)
        {
            if (String.IsNullOrWhiteSpace(frame)) return;
            OpenCodeSseEvent item; string error;
            if (!TryParseSseFrame(frame, _json, out item, out error)) { MarkAmbiguous("malformed_sse_frame: " + error); return; }
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
                    MarkAmbiguousLocked("project_directory_mismatch");
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
                // PERF-002: rolling bounded buffer -- discard the oldest content
                // BEFORE appending instead of concatenating the whole retained
                // string and trimming the result.
                if (!String.IsNullOrEmpty(item.Text)) state.AppendText(item.Text, MaxTextChars);
                if (!String.IsNullOrEmpty(item.FailureCode)) { state.FailureCode = item.FailureCode; state.Status = OpenCodeStatusKind.Error; BindSessionLocked(item.SessionId); }
                if (item.Type == "session.status" && (item.Status == OpenCodeStatusKind.Busy || item.Status == OpenCodeStatusKind.Retry)) BindSessionLocked(item.SessionId);
                state.EvidenceSequence++;
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
                if (runtime.Status != OpenCodeStatusKind.Idle || _ambiguousGeneration >= 0 || !_connected || _reconcileRequired) { error = "live_signal_not_send_safe_before_commit"; return false; }
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
        private void BindSessionLocked(string id)
        {
            if (String.IsNullOrEmpty(_boundSessionId)) _boundSessionId = id;
            else if (!String.Equals(_boundSessionId, id, StringComparison.Ordinal)) MarkAmbiguousLocked("session_binding_conflict");
        }

        // CORE-002: guard against clearing ambiguity from a session whose
        // directory is not this transport's pinned project.
        private static bool ClearedByForeignProject(SessionSummary session, TargetRule rule)
        {
            return session == null || rule == null ||
                !String.Equals(NormalizePath(session.Directory), NormalizePath(rule.ProjectDirectory), StringComparison.OrdinalIgnoreCase);
        }

        // PERF-001: begin (or reuse) the bounded per-generation REST snapshot.
        private ObservationSnapshot BeginSnapshot(DateTime nowUtc)
        {
            lock (_gate)
            {
                if (_snapshot != null && (nowUtc - _snapshot.TakenUtc).TotalMilliseconds < SnapshotLifetimeMs && _snapshot.StreamGeneration == _streamGeneration)
                {
                    return _snapshot;
                }
                return NewSnapshotLocked(nowUtc);
            }
        }

        /// <summary>
        /// PERF-001: candidate validation always opens a FRESH generation. Reusing
        /// the poll's evidence here would make the poll itself the authorization,
        /// which is a safety change, not an optimization. The saving comes from
        /// Send consuming this validation's ticket instead of repeating it.
        /// </summary>
        private ObservationSnapshot CurrentSnapshotOrNew()
        {
            lock (_gate)
            {
                return NewSnapshotLocked(DateTime.UtcNow);
            }
        }

        private ObservationSnapshot NewSnapshotLocked(DateTime nowUtc)
        {
            _snapshot = new ObservationSnapshot
            {
                Sequence = ++_snapshotSequence,
                TakenUtc = nowUtc,
                StreamGeneration = _streamGeneration
            };
            InvalidateValidationTicketLocked();
            return _snapshot;
        }

        private bool TryGetHealthCached(ObservationSnapshot snapshot, string endpoint, out HealthPayload health, out string error)
        {
            if (snapshot != null && snapshot.HealthChecked)
            {
                health = snapshot.Health;
                error = snapshot.HealthError;
                return snapshot.HealthOk;
            }
            bool ok = TryGetHealth(endpoint, out health, out error);
            if (snapshot != null)
            {
                snapshot.HealthChecked = true;
                snapshot.HealthOk = ok;
                snapshot.Health = health;
                snapshot.HealthError = error;
            }
            return ok;
        }

        private bool TryGetSessionsCached(ObservationSnapshot snapshot, TargetRule rule, out IList<SessionSummary> sessions, out string error)
        {
            if (snapshot != null && snapshot.SessionsChecked)
            {
                sessions = snapshot.Sessions;
                error = snapshot.SessionsError;
                return snapshot.SessionsOk;
            }
            bool ok = TryGetSessions(rule, out sessions, out error);
            if (snapshot != null)
            {
                snapshot.SessionsChecked = true;
                snapshot.SessionsOk = ok;
                snapshot.Sessions = sessions;
                snapshot.SessionsError = error;
            }
            return ok;
        }

        private bool TryGetStatusesCached(ObservationSnapshot snapshot, TargetRule rule, out Dictionary<string, OpenCodeStatusKind> statuses, out string error)
        {
            if (snapshot != null && snapshot.StatusesChecked)
            {
                statuses = snapshot.Statuses;
                error = snapshot.StatusesError;
                return snapshot.StatusesOk;
            }
            bool ok = TryGetStatuses(rule, out statuses, out error);
            if (snapshot != null)
            {
                snapshot.StatusesChecked = true;
                snapshot.StatusesOk = ok;
                snapshot.Statuses = statuses;
                snapshot.StatusesError = error;
            }
            return ok;
        }

        private bool TryGetSessionCached(ObservationSnapshot snapshot, TargetRule rule, string sessionId, out SessionSummary session, out string error)
        {
            if (snapshot != null && snapshot.SingleSessionChecked && String.Equals(snapshot.SingleSessionId, sessionId, StringComparison.Ordinal))
            {
                session = snapshot.SingleSession;
                error = snapshot.SingleSessionError;
                return snapshot.SingleSessionOk;
            }
            bool ok = TryGetSession(rule, sessionId, out session, out error);
            if (snapshot != null)
            {
                snapshot.SingleSessionChecked = true;
                snapshot.SingleSessionId = sessionId;
                snapshot.SingleSessionOk = ok;
                snapshot.SingleSession = session;
                snapshot.SingleSessionError = error;
            }
            return ok;
        }

        /// <summary>
        /// PERF-001: message history is failure EVIDENCE, not a lifecycle fact,
        /// so it is refetched only when the live SSE evidence for this session
        /// actually advanced (or the stream generation changed, or nothing has
        /// been fetched yet). An idle bound session no longer pulls 20 messages
        /// every two seconds.
        /// </summary>
        private bool TryGetMessagesCached(
            ObservationSnapshot snapshot,
            TargetRule rule,
            string sessionId,
            int streamGeneration,
            long evidenceSequence,
            out MessageReconciliation messages,
            out string error)
        {
            lock (_gate)
            {
                if (_messageCacheSession != null &&
                    String.Equals(_messageCacheSession, sessionId, StringComparison.Ordinal) &&
                    _messageCacheStreamGeneration == streamGeneration &&
                    _messageCacheEvidenceSequence == evidenceSequence &&
                    _messageCacheValid)
                {
                    messages = _messageCache;
                    error = null;
                    return true;
                }
            }
            bool ok = TryGetMessages(rule, sessionId, out messages, out error);
            lock (_gate)
            {
                _messageCacheSession = sessionId;
                _messageCacheStreamGeneration = streamGeneration;
                _messageCacheEvidenceSequence = evidenceSequence;
                _messageCacheValid = ok;
                _messageCache = ok ? messages : null;
            }
            if (snapshot != null) snapshot.MessagesFetched = true;
            return ok;
        }

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
        /// <summary>
        /// PERF-002: retrieve or create the runtime record, then bound the table.
        /// Never evicts the bound session, a session with a live/pending send, or
        /// a session still carrying failure/ambiguity authority.
        /// </summary>
        private OpenCodeRuntimeSession GetRuntimeSessionLocked(string id)
        {
            OpenCodeRuntimeSession state;
            if (!_sessions.TryGetValue(id, out state))
            {
                state = new OpenCodeRuntimeSession();
                _sessions[id] = state;
                _sessionOrder.Enqueue(id);
                EvictExcessSessionsLocked();
            }
            return state;
        }

        private void EvictExcessSessionsLocked()
        {
            int guard = 0;
            while (_sessions.Count > MaxRetainedSessions && _sessionOrder.Count > 0 && guard++ <= _sessionOrder.Count + MaxRetainedSessions)
            {
                string candidate = _sessionOrder.Dequeue();
                OpenCodeRuntimeSession existing;
                if (!_sessions.TryGetValue(candidate, out existing)) continue;
                if (IsProtectedSessionLocked(candidate, existing))
                {
                    // Re-queue: protected records stay, but they must not block
                    // eviction of the remaining unprotected ones forever.
                    _sessionOrder.Enqueue(candidate);
                    continue;
                }
                _sessions.Remove(candidate);
            }
        }

        private bool IsProtectedSessionLocked(string id, OpenCodeRuntimeSession state)
        {
            if (String.Equals(_boundSessionId, id, StringComparison.Ordinal)) return true;
            if (_send != null && String.Equals(_send.SessionId, id, StringComparison.Ordinal)) return true;
            if (state == null) return false;
            if (!String.IsNullOrEmpty(state.FailureCode)) return true;
            if (state.Status == OpenCodeStatusKind.Error || state.Status == OpenCodeStatusKind.Retry || state.Status == OpenCodeStatusKind.Busy) return true;
            return false;
        }

        internal int RetainedSessionCount { get { lock (_gate) { return _sessions.Count; } } }

        private Uri BuildUri(string path, bool projectQuery) { string value = (_endpoint ?? String.Empty).TrimEnd('/') + path; if (projectQuery) value += "?directory=" + Uri.EscapeDataString(_projectDirectory ?? String.Empty); return new Uri(value, UriKind.Absolute); }

        private bool TryHttp(string method, string endpoint, string path, string projectDirectory, string body, int timeoutMs, out int status, out string responseBody, out string error)
        {
            status = 0; responseBody = String.Empty; error = null;
            // PERF-001 / CORE-002 test seam: a deterministic in-process HTTP
            // layer with request counting. Production leaves this null and uses
            // the real HttpWebRequest path below.
            OpenCodeHttpInvoke over = HttpInvokeForTests;
            if (over != null)
            {
                return over(method, path, body, out status, out responseBody, out error);
            }
            bool isSend = String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path.IndexOf("/message", StringComparison.OrdinalIgnoreCase) >= 0;
            HttpWebRequest request = null;
            try
            {
                string url = endpoint.TrimEnd('/') + path; if (!String.IsNullOrEmpty(projectDirectory)) url += (path.IndexOf('?') >= 0 ? "&" : "?") + "directory=" + Uri.EscapeDataString(projectDirectory);
                request = (HttpWebRequest)WebRequest.Create(new Uri(url, UriKind.Absolute)); request.Method = method; request.Accept = "application/json"; request.ContentType = "application/json"; request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
                // PERF-001: allow connection reuse. KeepAlive=false forced a full
                // TCP setup/teardown for every REST call in a 2-second poll, which
                // is pure overhead against a loopback OpenCode server. Timeout and
                // abort semantics are unchanged; ServicePoint limits still bound
                // the pool.
                request.KeepAlive = true;
                if (isSend) lock (_gate) { _activeSendRequest = request; }
                else if (request != null) lock (_gate) { _activeRestRequests.Add(request); }
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
            finally
            {
                // W2L1-004: ordinary GETs are now registered as abortable too --
                // CancelInFlight() can break a worker blocked in a poll GET, not
                // only an SSE stream or a message POST.
                if (isSend) lock (_gate) { if (ReferenceEquals(_activeSendRequest, request)) _activeSendRequest = null; }
                else if (request != null) lock (_gate) { _activeRestRequests.Remove(request); }
            }
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

        // PERF-002: the retained text is a rolling window. Appending used to be
        // first + "\n" + second followed by a tail Substring, so every streamed
        // delta copied the whole 16K buffer twice.
        private sealed class OpenCodeRuntimeSession
        {
            private readonly StringBuilder _text = new StringBuilder();
            public OpenCodeStatusKind Status;
            public DateTime RetryAtUtc;
            public string FailureCode;
            public string LastEventId;
            public long EvidenceSequence;

            public void AppendText(string value, int maxChars)
            {
                if (String.IsNullOrEmpty(value)) return;
                if (_text.Length > 0) _text.Append('\n');
                _text.Append(value);
                int excess = _text.Length - maxChars;
                if (excess > 0) _text.Remove(0, excess);
            }

            public string TextSnapshot() { return _text.ToString(); }
        }

        private sealed class MessageReconciliation { public string Text; public string FailureCode; }

        // PERF-001: one bounded authoritative REST snapshot per poll generation.
        private sealed class ObservationSnapshot
        {
            public long Sequence;
            public DateTime TakenUtc;
            public int StreamGeneration;

            public bool HealthChecked, HealthOk;
            public HealthPayload Health;
            public string HealthError;

            public bool SessionsChecked, SessionsOk;
            public IList<SessionSummary> Sessions;
            public string SessionsError;

            public bool StatusesChecked, StatusesOk;
            public Dictionary<string, OpenCodeStatusKind> Statuses;
            public string StatusesError;

            public bool SingleSessionChecked, SingleSessionOk;
            public string SingleSessionId;
            public SessionSummary SingleSession;
            public string SingleSessionError;

            public bool MessagesFetched;
        }

        private sealed class SendOperation
        {
            public string OwnerRule;
            public string OwnerStateKey;
            public long OperationGeneration;
            public string SessionId;
            public string EndpointIdentity;
            public string Command;
            public TargetRule Rule;
            public string Agent;
            public string Provider;
            public string Model;
            public Func<bool> CommitAllowed;
            public volatile bool Completed;
            public SendOutcome Outcome;
            public string Error;
        }
    }
}
