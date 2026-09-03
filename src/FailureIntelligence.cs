using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SaiCont
{
    internal enum FailureClass
    {
        UNKNOWN,
        INTERNAL_ERROR,
        OVERLOADED,
        RATE_LIMIT,
        QUOTA_EXHAUSTED,
        AUTH_INVALID,
        PERMISSION_DENIED,
        MODEL_UNAVAILABLE,
        MODEL_NOT_FOUND,
        UPSTREAM_ERROR,
        TIMEOUT,
        NETWORK_ERROR,
        STREAM_INTERRUPTED,
        CONTEXT_LENGTH,
        OUTPUT_LIMIT,
        INVALID_REQUEST,
        TOOL_ERROR,
        CONTENT_FILTER,
        CLOUDFLARE_WAF,
        REGION_RESTRICTED,
        ACCOUNT_RESTRICTED,
        REQUEST_CANCELLED
    }

    internal enum FailureRecoveryAction
    {
        NONE,
        RETRY_SAME,
        RETRY_OTHER_PROVIDER,
        WAIT_FOR_RESET,
        STOP_AND_REPORT
    }

    internal sealed class FailureAssessment
    {
        public FailureClass Class;
        public bool Retryable;
        public string Severity;
        public FailureRecoveryAction RecommendedAction;
        public string[] MatchedBy;
        public int MatchStart;
        public int MatchLength;
        public string FingerprintMaterial;

        public bool IsKnown { get { return Class != FailureClass.UNKNOWN; } }
        public bool IsAutomaticRecovery
        {
            get
            {
                return RecommendedAction == FailureRecoveryAction.RETRY_SAME ||
                    RecommendedAction == FailureRecoveryAction.RETRY_OTHER_PROVIDER ||
                    RecommendedAction == FailureRecoveryAction.WAIT_FOR_RESET;
            }
        }
    }

    internal static class FailureIntelligence
    {
        private sealed class Candidate
        {
            public FailureClass Class;
            public string MatchedBy;
            public int Start;
            public int Length;
            public int SignalPriority;
            public string Value;
        }

        private sealed class MessageSignature
        {
            public FailureClass Class;
            public string Label;
            public Regex Pattern;
            public bool StrongWithoutEnvelope;
            public int SignalPriority;

            public MessageSignature(FailureClass failureClass, string label, string pattern, bool strongWithoutEnvelope)
                : this(failureClass, label, pattern, strongWithoutEnvelope, 3)
            {
            }

            public MessageSignature(FailureClass failureClass, string label, string pattern, bool strongWithoutEnvelope, int signalPriority)
            {
                Class = failureClass;
                Label = label;
                StrongWithoutEnvelope = strongWithoutEnvelope;
                SignalPriority = signalPriority;
                Pattern = Compile(pattern);
            }
        }

        private static readonly Regex HttpStatus = Compile(
            @"(?:(?:http(?:/\d(?:\.\d)?)?|status(?:_code)?|http_status)\s*[:=]?\s*(?<status>400|401|403|404|408|429|500|502|503|504)\b|\b(?<status>400)\s+bad\s+request\b|\b(?<status>401)\s+unauthorized\b|\b(?<status>403)\s+forbidden\b|\b(?<status>404)\s+(?:model\s+)?not\s+found\b|\b(?<status>408)\s+request\s+timeout\b|\b(?<status>429)\s+too\s+many\s+requests\b|\b(?<status>500)\s+internal\s+server\s+error\b|\b(?<status>502)\s+bad\s+gateway\b|\b(?<status>503)\s+service\s+unavailable\b|\b(?<status>504)\s+gateway\s+timeout\b)");

        private static readonly Regex StructuredCode = Compile(
            @"[\""']?(?:error[._-]?)?code[\""']?\s*[:=]\s*[\""']?(?<value>[A-Za-z][A-Za-z0-9_.-]{1,63}|400|401|403|404|408|429|500|502|503|504)[\""']?");

        private static readonly Regex StructuredType = Compile(
            @"[\""']?(?:error[._-]?)?type[\""']?\s*[:=]\s*[\""'](?<value>[A-Za-z][A-Za-z0-9_.-]{1,63})[\""']");

        private static readonly MessageSignature[] MessageSignatures =
        {
            new MessageSignature(FailureClass.INTERNAL_ERROR, "internal_error", @"\b(?:an\s+)?internal(?:\s+server)?\s+error(?:\s+occurred)?\b|\bsomething\s+went\s+wrong\b|\bunexpected\s+(?:server\s+)?error\b|\bservice\s+encountered\s+an\s+unexpected\s+condition\b", false),
            new MessageSignature(FailureClass.OVERLOADED, "overloaded", @"\b(?:service|server|model|system)?\s*(?:is\s+|currently\s+|temporarily\s+)?overload(?:ed)?\b|\bhigh\s+(?:demand|load)\b", false),
            new MessageSignature(FailureClass.RATE_LIMIT, "rate_limit", @"\brate[- ]?limit(?:ed)?\b|\btoo\s+many\s+(?:concurrent\s+)?requests\b|\b(?:tpm|rpm|concurrency)\s+limit\s+(?:reached|exceeded)\b|\brequest\s+throttled\b|\bplease\s+slow\s+down\b", false),
            new MessageSignature(FailureClass.QUOTA_EXHAUSTED, "quota_exhausted", @"\bquota\b.{0,48}\b(?:exceed(?:ed)?|exhaust(?:ed)?|limit)\b|\binsufficient\s+(?:quota|balance|credits?)\b|\b(?:balance|credits?)\s+(?:is\s+)?(?:exhausted|too\s+low)\b|\b(?:daily|weekly|monthly|usage)\s+limit\s+(?:reached|exceeded)\b|\bno\s+credits?\s+remaining\b|\bresource_exhausted\b", false),
            new MessageSignature(FailureClass.AUTH_INVALID, "auth_invalid", @"\binvalid\s+(?:api[- ]?key|bearer\s+token|token|authentication\s+credentials?)\b|\bincorrect\s+api[- ]?key\b|\bauthentication\s+failed\b|\bunauthori[sz]ed\b|\btoken\s+(?:is\s+invalid|has\s+expired)\b|\bmissing\s+api[- ]?key\b|\bno\s+api[- ]?key\s+provided\b", false),
            new MessageSignature(FailureClass.PERMISSION_DENIED, "permission_denied", @"\bpermission\s+denied\b|\baccess\s+denied\b|\bforbidden\b|\b(?:account|organization|project)\s+does\s+not\s+have\s+access\b|\bmodel\s+access\s+denied\b|\baccount\s+is\s+not\s+authorized\b", false),
            new MessageSignature(FailureClass.MODEL_UNAVAILABLE, "model_unavailable", @"\bmodel\s+(?:is\s+)?(?:currently\s+|temporarily\s+)?unavailable\b|\bno\s+(?:available\s+)?(?:model|channel|provider|healthy\s+provider|endpoints?|upstream)\b|\bunable\s+to\s+route\s+request\b|\bno\s+route\s+available\b|\bprovider\s+unavailable\b", false),
            new MessageSignature(FailureClass.MODEL_NOT_FOUND, "model_not_found", @"\bmodel\s+(?:was\s+)?not\s+found\b|\bmodel\s+does\s+not\s+exist\b|\bunknown\s+model\b|\binvalid\s+model\b|\bno\s+such\s+model\b|\bmodel_not_found\b", false),
            new MessageSignature(FailureClass.UPSTREAM_ERROR, "upstream_error", @"\bupstream\b.{0,48}\b(?:error|failed|unavailable)\b|\bprovider\s+(?:returned\s+an?\s+error|request\s+failed)\b|\bbad\s+gateway\b|\bgateway\s+error\b|\bproxy\s+error\b|\bbackend\s+error\b|\bnew_api_error\b", false),
            new MessageSignature(FailureClass.TIMEOUT, "timeout", @"\b(?:request|operation|connection|read|upstream|gateway)\s+timed?\s*out\b|\btimeout\s+awaiting\s+response\b|\bdeadline\s+exceeded\b|\bcontext\s+deadline\s+exceeded\b|\betimedout\b", false),
            new MessageSignature(FailureClass.NETWORK_ERROR, "network_error", @"\bnetwork\s+error\b|\bconnection\s+(?:error|reset|refused|closed|aborted)\b|\bremote\s+host\s+closed\s+the\s+connection\b|\bsocket\s+hang\s+up\b|\beconnreset\b|\beconnrefused\b|\benetunreach\b|\behostunreach\b|\bdns\s+lookup\s+failed\b|\bcould\s+not\s+resolve\s+host\b|\btemporary\s+failure\s+in\s+name\s+resolution\b", true, 4),
            new MessageSignature(FailureClass.STREAM_INTERRUPTED, "stream_interrupted", @"\bstream\s+(?:interrupted|disconnected|closed\s+unexpectedly)\b|\bunexpected\s+end\s+of\s+stream\b|\bconnection\s+closed\s+before\s+full\s+response\b|\bincomplete\s+chunked\s+encoding\b|\bresponse\s+ended\s+prematurely\b|\bsse\s+connection\s+closed\b|\bunexpected\s+eof\b", false, 4),
            new MessageSignature(FailureClass.CONTEXT_LENGTH, "context_length", @"\b(?:maximum\s+)?context\s+length\s+exceeded\b|\bprompt\s+is\s+too\s+long\b|\binput\s+is\s+too\s+long\b|\brequest\s+too\s+large\b|\btoo\s+many\s+tokens\b|\b(?:maximum\s+)?token\s+limit\s+exceeded\b|\bcontext_length_exceeded\b|\binput\s+exceeds\s+the\s+model'?s\s+context\s+window\b", false),
            new MessageSignature(FailureClass.OUTPUT_LIMIT, "output_limit", @"\bmaximum\s+output\s+tokens\s+reached\b|\bmax_(?:output_)?tokens\s+reached\b|\boutput\s+token\s+limit\s+reached\b|\bresponse\s+truncated\b|\bfinish_reason\s*:\s*length\b|\bstop_reason\s*:\s*max_tokens\b", false),
            new MessageSignature(FailureClass.INVALID_REQUEST, "invalid_request", @"\binvalid\s+request\b|\bbad\s+request\b|\bmalformed\s+request\b|\binvalid\s+json\b|\bcould\s+not\s+parse\s+request\s+body\b|\bmissing\s+required\s+parameter\b|\b(?:invalid|unsupported|unknown)\s+parameter\b|\binvalid\s+value\b|\binvalid_request_error\b", false),
            new MessageSignature(FailureClass.TOOL_ERROR, "tool_error", @"\binvalid\s+tool\s+(?:call|schema|result)\b|\btool\s+(?:call|execution)\s+failed\b|\binvalid\s+function\s+schema\b|\bfunction\s+call\s+failed\b|\bunknown\s+tool\b|\btool\s+not\s+found\b|\bmissing\s+tool\s+result\b|\btool_use_error\b", false),
            new MessageSignature(FailureClass.CONTENT_FILTER, "content_filter", @"\bcontent\s+(?:policy\s+violation|blocked|filter\s+triggered)\b|\b(?:request|response)\s+blocked\s+by\s+safety\s+system\b|\bsafety\s+filter\s+triggered\b|\bblocked\s+by\s+moderation\b|\bresponsible\s+ai\s+policy\s+violation\b|\bprohibited_content\b|\bfinish_reason\s*:\s*content_filter\b", false),
            new MessageSignature(FailureClass.CLOUDFLARE_WAF, "cloudflare_waf", @"\battention\s+required!\s*\|\s*cloudflare\b|\bsorry,?\s+you\s+have\s+been\s+blocked\b|\bcloudflare\s+ray\s+id\b|\bchecking\s+your\s+browser\s+before\s+accessing\b|\bchallenge-platform\b|\bcf-error-details\b", true),
            new MessageSignature(FailureClass.REGION_RESTRICTED, "region_restricted", @"\bservice\s+is\s+not\s+available\s+in\s+your\s+region\b|\bunsupported\s+(?:country|region)\b|\bservice\s+is\s+unavailable\s+in\s+your\s+location\b|\baccess\s+from\s+your\s+region\s+is\s+not\s+supported\b|\bcountry\s+not\s+supported\b", false),
            new MessageSignature(FailureClass.ACCOUNT_RESTRICTED, "account_restricted", @"\baccount\s+(?:suspended|disabled|restricted)\b|\baccount\s+has\s+been\s+deactivated\b|\baccount\s+is\s+not\s+active\b|\borganization\s+suspended\b|\bproject\s+disabled\b", false),
            new MessageSignature(FailureClass.REQUEST_CANCELLED, "request_cancelled", @"\brequest\s+cancel+ed\b|\boperation\s+cancel+ed\b|\bcancel+ed\s+by\s+client\b|\baborterror\b|\boperation\s+was\s+aborted\b", false, 4)
        };

        public static FailureAssessment Classify(string rawText)
        {
            if (String.IsNullOrWhiteSpace(rawText))
            {
                return Unknown();
            }

            var candidates = new List<Candidate>();
            AddHttpCandidates(rawText, candidates);
            AddStructuredCandidates(rawText, StructuredCode, 1, "code", candidates);
            AddStructuredCandidates(rawText, StructuredType, 2, "type", candidates);
            AddMessageCandidates(rawText, candidates);

            if (candidates.Count == 0)
            {
                return Unknown();
            }

            Candidate selected = candidates[0];
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Start > selected.Start)
                {
                    selected = candidate;
                }
            }

            // The latest console event wins across events. Inside that event's
            // line, structured evidence follows the fixed precedence contract:
            // HTTP status, code, type, then message/transport signature.
            int selectedLineStart = rawText.LastIndexOf('\n', Math.Max(0, selected.Start - 1));
            selectedLineStart = selectedLineStart < 0 ? 0 : selectedLineStart + 1;
            int selectedLineEnd = rawText.IndexOf('\n', selected.Start + selected.Length);
            selectedLineEnd = selectedLineEnd < 0 ? rawText.Length : selectedLineEnd;
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Start < selectedLineStart || candidate.Start > selectedLineEnd)
                {
                    continue;
                }
                if (candidate.SignalPriority < selected.SignalPriority ||
                    (candidate.SignalPriority == selected.SignalPriority && candidate.Start > selected.Start))
                {
                    selected = candidate;
                }
            }

            var evidence = new List<string>();
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Class != selected.Class || Math.Abs(candidate.Start - selected.Start) > 512)
                {
                    continue;
                }
                if (!evidence.Contains(candidate.MatchedBy))
                {
                    evidence.Add(candidate.MatchedBy);
                }
                if (evidence.Count == 6)
                {
                    break;
                }
            }

            FailureRecoveryAction action = ActionFor(selected.Class);
            return new FailureAssessment
            {
                Class = selected.Class,
                Retryable = IsRetryable(selected.Class),
                Severity = selected.Class == FailureClass.REQUEST_CANCELLED ? "cancelled" :
                    (action == FailureRecoveryAction.WAIT_FOR_RESET ? "waiting" :
                    (action == FailureRecoveryAction.STOP_AND_REPORT ? "permanent" : "transient")),
                RecommendedAction = action,
                MatchedBy = evidence.ToArray(),
                MatchStart = selected.Start,
                MatchLength = selected.Length,
                FingerprintMaterial = selected.Class.ToString() + ":" + NormalizeForFingerprint(selected.Value)
            };
        }

        internal static string NormalizeForFingerprint(string value)
        {
            string normalized = (value ?? String.Empty).ToLowerInvariant().Replace("\r", " ").Replace("\n", " ");
            normalized = Regex.Replace(normalized, @"\b(?:request|req|trace|correlation|ray)[ _-]?(?:id)?\s*[:=]?\s*[a-z0-9_.-]{6,}\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\b\d{4}-\d{2}-\d{2}[t ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:z|[+-]\d{2}:?\d{2})?\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"^\s*(?:generate_stream\s+from\s+[^:]{1,80}|openrouter|openai|anthropic|google|gemini|azure|bedrock|vertex|provider)\s*[:>-]\s*", String.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"[\(\[\{]\s*[\)\]\}]", " ", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            return normalized;
        }

        private static void AddHttpCandidates(string text, IList<Candidate> candidates)
        {
            Candidate latest = null;
            foreach (Match match in HttpStatus.Matches(text))
            {
                if (!IsLikelyFailureEnvelope(text, match))
                {
                    continue;
                }
                int status;
                if (!Int32.TryParse(match.Groups["status"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out status))
                {
                    continue;
                }
                FailureClass mapped = MapHttpStatus(status);
                if (mapped != FailureClass.UNKNOWN)
                {
                    latest = NewCandidate(mapped, "http_status:" + status, match, 0);
                }
            }
            if (latest != null)
            {
                candidates.Add(latest);
            }
        }

        private static void AddStructuredCandidates(string text, Regex regex, int priority, string signal, IList<Candidate> candidates)
        {
            Candidate latest = null;
            foreach (Match match in regex.Matches(text))
            {
                if (!IsLikelyStructuredEnvelope(text, match))
                {
                    continue;
                }
                string value = match.Groups["value"].Value;
                FailureClass mapped = MapStructuredValue(value);
                // A structured but unknown code/type is still authoritative:
                // it prevents a weaker generic message in the same payload
                // from being guessed into an automatic retry class.
                latest = NewCandidate(mapped, signal + ":" + NormalizeToken(value), match, priority);
            }
            if (latest != null)
            {
                candidates.Add(latest);
            }
        }

        private static void AddMessageCandidates(string text, IList<Candidate> candidates)
        {
            foreach (MessageSignature signature in MessageSignatures)
            {
                Candidate latest = null;
                foreach (Match match in signature.Pattern.Matches(text))
                {
                    if (signature.StrongWithoutEnvelope || IsLikelyFailureEnvelope(text, match))
                    {
                        latest = NewCandidate(signature.Class, "message:" + signature.Label, match, signature.SignalPriority);
                    }
                }
                if (latest != null)
                {
                    candidates.Add(latest);
                }
            }
        }

        private static bool IsLikelyFailureEnvelope(string text, Match match)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = text.IndexOf('\n', match.Index + match.Length);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            string line = text.Substring(lineStart, lineEnd - lineStart).Trim();
            if (line.IndexOf("(?i)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("<pattern>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("AssertEqual", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            if (Regex.IsMatch(line, @"[\""'](?:message|error|code|type|status)[\""']\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
            if (Regex.IsMatch(line, @"^[\s|>\-\[\]{}]*(?:the\s+)?(?:error|fatal|provider|upstream|server|service|model|network|connection|stream|gateway|backend)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
            string compactLine = NormalizeForFingerprint(line).Trim(' ', '.', '!', ':', '-', '"', '\'');
            string compactMatch = NormalizeForFingerprint(match.Value).Trim(' ', '.', '!', ':', '-', '"', '\'');
            return compactLine.Length <= compactMatch.Length + 48 &&
                compactLine.StartsWith(compactMatch, StringComparison.Ordinal);
        }

        private static bool IsLikelyStructuredEnvelope(string text, Match match)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = text.IndexOf('\n', match.Index + match.Length);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            string line = text.Substring(lineStart, lineEnd - lineStart).Trim();
            if (line.IndexOf("(?i)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("<pattern>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("AssertEqual", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            return line.StartsWith("{", StringComparison.Ordinal) ||
                Regex.IsMatch(line, @"^[\s|>\-\[\]]*(?:error|fatal|provider|upstream|server|service|model|gateway|backend|generate_stream)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static Candidate NewCandidate(FailureClass failureClass, string matchedBy, Match match, int priority)
        {
            return new Candidate
            {
                Class = failureClass,
                MatchedBy = matchedBy,
                Start = match.Index,
                Length = match.Length,
                SignalPriority = priority,
                Value = match.Value
            };
        }

        private static FailureClass MapHttpStatus(int status)
        {
            switch (status)
            {
                case 400: return FailureClass.INVALID_REQUEST;
                case 401: return FailureClass.AUTH_INVALID;
                case 403: return FailureClass.PERMISSION_DENIED;
                case 404: return FailureClass.MODEL_NOT_FOUND;
                case 408: return FailureClass.TIMEOUT;
                case 429: return FailureClass.RATE_LIMIT;
                case 500: return FailureClass.INTERNAL_ERROR;
                case 502:
                case 503:
                case 504: return FailureClass.UPSTREAM_ERROR;
                default: return FailureClass.UNKNOWN;
            }
        }

        private static FailureClass MapStructuredValue(string rawValue)
        {
            int status;
            if (Int32.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out status))
            {
                return MapHttpStatus(status);
            }
            string value = NormalizeToken(rawValue);
            if (value == "internal_error" || value == "server_error") return FailureClass.INTERNAL_ERROR;
            if (value.Contains("overload")) return FailureClass.OVERLOADED;
            if (value.Contains("rate_limit") || value == "throttled") return FailureClass.RATE_LIMIT;
            if (value.Contains("quota") || value == "resource_exhausted" || value.Contains("balance")) return FailureClass.QUOTA_EXHAUSTED;
            if (value.Contains("auth") || value.Contains("api_key") || value == "unauthorized" || value.Contains("token_expired")) return FailureClass.AUTH_INVALID;
            if (value == "permission_denied" || value == "forbidden") return FailureClass.PERMISSION_DENIED;
            if (value.Contains("model_unavailable") || value.Contains("provider_unavailable")) return FailureClass.MODEL_UNAVAILABLE;
            if (value.Contains("model_not_found") || value == "unknown_model") return FailureClass.MODEL_NOT_FOUND;
            if (value.Contains("upstream") || value == "new_api_error" || value == "bad_gateway") return FailureClass.UPSTREAM_ERROR;
            if (value.Contains("timeout") || value == "deadline_exceeded" || value == "etimedout") return FailureClass.TIMEOUT;
            if (value.Contains("network") || value == "econnreset" || value == "econnrefused" || value == "enetunreach" || value == "ehostunreach") return FailureClass.NETWORK_ERROR;
            if (value.Contains("stream") || value == "unexpected_eof") return FailureClass.STREAM_INTERRUPTED;
            if (value.Contains("context_length") || value.Contains("token_limit")) return FailureClass.CONTEXT_LENGTH;
            if (value.Contains("output_limit") || value == "max_tokens") return FailureClass.OUTPUT_LIMIT;
            if (value.Contains("invalid_request") || value == "bad_request") return FailureClass.INVALID_REQUEST;
            if (value.Contains("tool") || value.Contains("function_call")) return FailureClass.TOOL_ERROR;
            if (value.Contains("content_filter") || value == "prohibited_content") return FailureClass.CONTENT_FILTER;
            if (value.Contains("cloudflare") || value.Contains("waf")) return FailureClass.CLOUDFLARE_WAF;
            if (value.Contains("region") || value.Contains("country")) return FailureClass.REGION_RESTRICTED;
            if (value.Contains("account") || value.Contains("organization_suspended") || value == "project_disabled") return FailureClass.ACCOUNT_RESTRICTED;
            if (value.Contains("cancel") || value == "aborterror" || value.Contains("aborted")) return FailureClass.REQUEST_CANCELLED;
            return FailureClass.UNKNOWN;
        }

        private static string NormalizeToken(string value)
        {
            return Regex.Replace((value ?? String.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');
        }

        private static bool IsRetryable(FailureClass failureClass)
        {
            if (failureClass == FailureClass.REQUEST_CANCELLED)
            {
                // Transport-wise retryable, but not automatically retried:
                // cancellation may be an explicit operator action.
                return true;
            }
            FailureRecoveryAction action = ActionFor(failureClass);
            return action == FailureRecoveryAction.RETRY_SAME || action == FailureRecoveryAction.RETRY_OTHER_PROVIDER;
        }

        private static FailureRecoveryAction ActionFor(FailureClass failureClass)
        {
            switch (failureClass)
            {
                case FailureClass.INTERNAL_ERROR:
                case FailureClass.OVERLOADED:
                case FailureClass.TIMEOUT:
                case FailureClass.NETWORK_ERROR:
                case FailureClass.STREAM_INTERRUPTED:
                    return FailureRecoveryAction.RETRY_SAME;
                case FailureClass.RATE_LIMIT:
                case FailureClass.MODEL_UNAVAILABLE:
                case FailureClass.UPSTREAM_ERROR:
                case FailureClass.CLOUDFLARE_WAF:
                    return FailureRecoveryAction.RETRY_OTHER_PROVIDER;
                case FailureClass.QUOTA_EXHAUSTED:
                    return FailureRecoveryAction.WAIT_FOR_RESET;
                case FailureClass.UNKNOWN:
                    return FailureRecoveryAction.NONE;
                default:
                    return FailureRecoveryAction.STOP_AND_REPORT;
            }
        }

        private static FailureAssessment Unknown()
        {
            return new FailureAssessment
            {
                Class = FailureClass.UNKNOWN,
                Retryable = false,
                Severity = "unknown",
                RecommendedAction = FailureRecoveryAction.NONE,
                MatchedBy = new string[0],
                MatchStart = -1,
                MatchLength = 0,
                FingerprintMaterial = String.Empty
            };
        }

        private static Regex Compile(string pattern)
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TargetRule.DefaultRegexTimeout);
        }
    }
}
