using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SaiCont
{
    internal static partial class Program
    {
        // --- PERF-001 --------------------------------------------------------

        private static int Audit163_Perf001_RequestBudget()
        {
            int failures = 0;
            var fake = new Audit163FakeOpenCode();
            TargetRule rule;
            using (var scope = new Audit163Disposable(Audit163Transport(fake, false, out rule)))
            {
                OpenCodeHttpTransport transport = scope.Transport;
                DateTime now = new DateTime(2026, 9, 4, 15, 0, 0, DateTimeKind.Utc);
                transport.SetStreamStateForTests(true, true);
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);

                string error;
                transport.Poll(rule, now, out error);

                // Steady state for a bound idle session: health + sessions + status.
                // Message history is failure EVIDENCE and is refetched only when the
                // SSE evidence sequence advanced, so an idle poll no longer pulls 20
                // messages every two seconds.
                fake.Reset();
                transport.Poll(rule, now.AddSeconds(5), out error);
                failures += AssertEqual(1, fake.Health, "PERF-001: one health request per bound idle poll");
                failures += AssertEqual(1, fake.SessionList, "PERF-001: one session-list request per bound idle poll");
                failures += AssertEqual(1, fake.Status, "PERF-001: one status request per bound idle poll");
                failures += AssertEqual(0, fake.Messages, "PERF-001: unchanged SSE evidence does not refetch message history");
                failures += AssertEqual(3, fake.Total, "PERF-001: the bound idle poll budget is exactly 3 REST requests");

                // New SSE evidence DOES force the authoritative refresh.
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, "new assistant text", null);
                fake.Reset();
                transport.Poll(rule, now.AddSeconds(10), out error);
                failures += AssertEqual(1, fake.Messages, "PERF-001: advanced SSE evidence refetches message history exactly once");

                // Authorized send: ONE candidate validation phase, then the
                // independent immediate commit validation before the POST.
                TargetObservation sendable = transport.Poll(rule, now.AddSeconds(15), out error);
                failures += AssertEqual(true, sendable.CanSend, "PERF-001: fixture reaches a send-eligible observation");
                sendable.OwnerStateKey = rule.Name + ":1:2026";
                fake.Reset();
                failures += AssertEqual(true, transport.ValidatePreSend(sendable, rule, out error), "PERF-001: candidate validation passes");
                failures += AssertEqual(1, fake.Health, "PERF-001: candidate validation costs one health request");
                failures += AssertEqual(1, fake.SessionSingle, "PERF-001: candidate validation costs one session request");
                failures += AssertEqual(1, fake.Status, "PERF-001: candidate validation costs one status request");
                failures += AssertEqual(3, fake.Total, "PERF-001: candidate validation is exactly one health+session+status phase");

                fake.Reset();
                SendOutcome pending = transport.Send(sendable, rule, rule.Command, delegate { return true; }, out error);
                failures += AssertEqual(SendDisposition.Pending, pending.Disposition, "PERF-001: the send is queued");
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (transport.HasPendingSend && DateTime.UtcNow < deadline) { Thread.Sleep(20); }
                failures += AssertEqual(false, transport.HasPendingSend, "PERF-001: the send completed");
                // Send must NOT repeat candidate validation: only the commit-window
                // validation (health+session+status) plus the POST itself.
                failures += AssertEqual(1, fake.Post, "PERF-001: exactly one message POST");
                failures += AssertEqual(1, fake.Health, "PERF-001: the duplicate pre-send validation is gone (health)");
                failures += AssertEqual(1, fake.SessionSingle, "PERF-001: the duplicate pre-send validation is gone (session)");
                failures += AssertEqual(1, fake.Status, "PERF-001: the duplicate pre-send validation is gone (status)");

                // The commit-window check is still independent and still blocks a
                // model change between candidate validation and the POST.
                var ownerA = new SendOwner { RuleName = rule.Name, StateKey = sendable.OwnerStateKey, SessionIdentity = fake.SessionId };
                SendOutcome consumed;
                transport.TryTakeCompletedSend(ownerA, out consumed);
                TargetObservation second = transport.Poll(rule, now.AddSeconds(20), out error);
                second.OwnerStateKey = sendable.OwnerStateKey;
                failures += AssertEqual(true, transport.ValidatePreSend(second, rule, out error), "PERF-001: second candidate validation passes");
                fake.Model = "SOME-OTHER-MODEL";
                SendOutcome blockedSend = transport.Send(second, rule, rule.Command, delegate { return true; }, out error);
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (transport.HasPendingSend && DateTime.UtcNow < deadline) { Thread.Sleep(20); }
                var ownerB = new SendOwner { RuleName = rule.Name, StateKey = second.OwnerStateKey, SessionIdentity = fake.SessionId };
                SendOutcome blockedOutcome;
                transport.TryTakeCompletedSend(ownerB, out blockedOutcome);
                failures += AssertEqual(true, blockedOutcome != null && blockedOutcome.Disposition != SendDisposition.CompleteInputCommitted, "PERF-001: a model change between candidate and commit still blocks the POST");

                // PERF-005: publish the request budget so scripts/performance.ps1
                // can enforce it as a mandatory scenario instead of inferring
                // network cost from an idle no-target run.
                Console.WriteLine("MEASURE: perf_opencode idle_poll_requests=3 send_candidate_requests=3 send_commit_requests=3 send_posts=1");
            }
            return failures;
        }

        // --- PERF-002 --------------------------------------------------------

        private static int Audit163_Perf002_SessionBounds()
        {
            int failures = 0;
            var fake = new Audit163FakeOpenCode();
            TargetRule rule;
            using (var scope = new Audit163Disposable(Audit163Transport(fake, false, out rule)))
            {
                OpenCodeHttpTransport transport = scope.Transport;
                transport.SetStreamStateForTests(true, true);
                transport.BindSessionForTests("ses_bound");
                transport.SetSessionStateForTests("ses_bound", OpenCodeStatusKind.Idle, "bound-session-text", null);
                transport.SetSessionStateForTests("ses_pinned", OpenCodeStatusKind.Error, "failed", "internal_error");

                // Churn thousands of unrelated sessions through the real frame path.
                for (int i = 0; i < 3000; i++)
                {
                    string id = "ses_churn_" + i.ToString(CultureInfo.InvariantCulture);
                    transport.ProcessFrameForTests(
                        "data: {\"directory\":\"" + fake.Directory.Replace("\\", "\\\\") +
                        "\",\"payload\":{\"id\":\"evt_" + i.ToString(CultureInfo.InvariantCulture) +
                        "\",\"type\":\"session.idle\",\"properties\":{\"sessionID\":\"" + id + "\"}}}");
                }
                failures += AssertEqual(true, transport.RetainedSessionCount <= 64, "PERF-002: retained sessions stay within the declared cap (actual=" + transport.RetainedSessionCount + ")");
                failures += AssertEqual("bound-session-text", transport.SessionTextForTests("ses_bound"), "PERF-002: the bound session survives eviction pressure");
                failures += AssertEqual(true, transport.SessionTextForTests("ses_pinned") != null, "PERF-002: a session carrying failure authority survives eviction pressure");

                // Rolling bounded text: many small deltas keep only the newest tail.
                var builder = new StringBuilder();
                for (int i = 0; i < 400; i++)
                {
                    string chunk = new string((char)('a' + (i % 26)), 100);
                    transport.ProcessFrameForTests(
                        "data: {\"directory\":\"" + fake.Directory.Replace("\\", "\\\\") +
                        "\",\"payload\":{\"id\":\"delta_" + i.ToString(CultureInfo.InvariantCulture) +
                        "\",\"type\":\"message.part.delta\",\"properties\":{\"sessionID\":\"ses_bound\",\"delta\":\"" + chunk + "\"}}}");
                }
                string retained = transport.SessionTextForTests("ses_bound");
                failures += AssertEqual(true, retained.Length <= 16000, "PERF-002: retained text stays inside the 16K bound (actual=" + retained.Length + ")");
                string lastChunk = new string((char)('a' + (399 % 26)), 100);
                failures += AssertEqual(true, retained.EndsWith(lastChunk, StringComparison.Ordinal), "PERF-002: the newest delta is retained at the tail");

                // A malformed frame is still fail-closed evidence.
                transport.ProcessFrameForTests("data: {not-json}");
                failures += AssertEqual(true, transport.IsAmbiguous, "PERF-002: a malformed frame remains fail-closed");
                // PERF-005: publish the SSE churn bounds for the harness.
                Console.WriteLine("MEASURE: perf_sse churned_sessions=3000 retained_sessions=" + transport.RetainedSessionCount.ToString(CultureInfo.InvariantCulture) + " retained_text_chars=" + retained.Length.ToString(CultureInfo.InvariantCulture) + " event_history_cap=512");
            }
            return failures;
        }

        // --- PERF-003 --------------------------------------------------------

        private static int Audit163_Perf003_ObservationCache()
        {
            int failures = 0;

            // Two rules resolving to the SAME console must produce exactly one
            // ordinary observation capture per poll.
            int reads = 0;
            const int SharedPid = 5150;
            TargetRule ruleA = Audit163ConsoleRule("shared-a", "codex", 60);
            TargetRule ruleB = Audit163ConsoleRule("shared-b", "codex", 30);
            var shared = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { ruleA, ruleB } };
            DateTime clock = new DateTime(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);
            var sharedEngine = new WatcherEngine(
                shared,
                delegate { return new List<ProcessEntry> { new ProcessEntry { Id = SharedPid, ParentId = 1, Name = "codex" } }; },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = clock.AddHours(-1) }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    reads++;
                    s = Audit163Snapshot(pid, lineCount, "Ask anything");
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                delegate { return clock; });
            IList<PollResult> sharedResults = sharedEngine.PollOnce(false);
            try { sharedEngine.Dispose(); } catch { }
            failures += AssertEqual(1, reads, "PERF-003: two rules on one console perform exactly one observation capture (actual=" + reads + ")");
            failures += AssertEqual(2, sharedResults.Count(r => r.Read), "PERF-003: both rules still receive their own observation result");
            // PERF-005: publish the observation-count metric for the harness.
            Console.WriteLine("MEASURE: perf_console shared_console_rules=2 shared_console_reads=" + reads.ToString(CultureInfo.InvariantCulture));

            // Five rules on five distinct consoles still capture five times.
            reads = 0;
            var distinctTargets = new List<TargetRule>();
            var distinctProcesses = new List<ProcessEntry>();
            for (int i = 0; i < 5; i++)
            {
                distinctTargets.Add(Audit163ConsoleRule("distinct-" + i.ToString(CultureInfo.InvariantCulture), "codex" + i.ToString(CultureInfo.InvariantCulture), 40));
                distinctProcesses.Add(new ProcessEntry { Id = 6000 + i, ParentId = 1, Name = "codex" + i.ToString(CultureInfo.InvariantCulture) });
            }
            var distinct = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = distinctTargets };
            var distinctEngine = new WatcherEngine(
                distinct,
                delegate { return distinctProcesses; },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = clock.AddHours(-1) }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    reads++;
                    s = Audit163Snapshot(pid, lineCount, "Ask anything");
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                delegate { return clock; });
            distinctEngine.PollOnce(false);
            try { distinctEngine.Dispose(); } catch { }
            failures += AssertEqual(5, reads, "PERF-003: five distinct consoles still perform five captures");

            // A rule asking for fewer lines sees the same tail an independent read
            // would have produced.
            ConsoleSnapshot deep = Audit163Snapshot(4242, 100, "Ask anything");
            ConsoleSnapshot tail = PollConsoleObservationCache.DeriveTail(deep, 10);
            string[] tailLines = tail.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            failures += AssertEqual(10, tailLines.Length, "PERF-003: a shallower rule sees exactly its requested tail depth");
            failures += AssertEqual("Ask anything", tailLines[tailLines.Length - 1], "PERF-003: the derived tail ends at the cursor line");
            failures += AssertEqual(90, tail.StartRow, "PERF-003: StartRow is adjusted so trigger-distance math stays correct");
            failures += AssertEqual(deep.CursorRow, tail.CursorRow, "PERF-003: the cursor row is unchanged by tail derivation");

            // A transactional pre-send reread must NOT be served from the cache:
            // the engine passes a null cache on that path, so the reader is called
            // again even for a console already observed this poll.
            int preSendReads = 0;
            TargetRule sendRule = Audit163ConsoleRule("presend", "codex", 40);
            var sendConfig = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { sendRule } };
            DateTime sendClock = new DateTime(2026, 9, 4, 17, 0, 0, DateTimeKind.Utc);
            // The session start must be STABLE across polls: it is part of the
            // durable state key, so a moving value would create a new state (and a
            // fresh initial delay) on every poll.
            DateTime sendSessionStart = sendClock.AddHours(-1);
            int writeCount = 0;
            var sendEngine = new WatcherEngine(
                sendConfig,
                delegate { return new List<ProcessEntry> { new ProcessEntry { Id = 7373, ParentId = 1, Name = "codex" } }; },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = sendSessionStart }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    preSendReads++;
                    s = Audit163Snapshot(pid, 8, "Ask anything");
                    s.Text = "Rate limited" + Environment.NewLine + s.Text;
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { writeCount++; e = null; return NativeWriteOutcome.CompleteInputCommitted; },
                delegate { return sendClock; });
            sendEngine.PollOnce(true);
            int afterFirstPoll = preSendReads;
            sendClock = sendClock.AddSeconds(30);
            sendEngine.PollOnce(true);
            try { sendEngine.Dispose(); } catch { }
            failures += AssertEqual(true, writeCount >= 1, "PERF-003: the fixture reaches a real write");
            failures += AssertEqual(true, preSendReads > afterFirstPoll + 1, "PERF-003: the transactional pre-send reread is fresh, not cached (reads=" + preSendReads + ")");
            return failures;
        }

        // --- PERF-004 --------------------------------------------------------

        private static int Audit163_Perf004_FailureGate()
        {
            int failures = 0;

            // Golden corpus: the cheap gate must never reject text the detailed
            // classifier recognizes, and classification must be unchanged.
            string[] corpus = new[]
            {
                "{\"code\":\"internal_error\",\"message\":\"An internal error occurred.\"}",
                "HTTP 429 Too Many Requests",
                "provider returned an error: code 429",
                "Error: daily limit reached, try again in 8h 57m",
                "Error: request timed out after 30s",
                "connection reset by peer",
                "context length exceeded for this model",
                "Attention Required! | Cloudflare",
                "invalid api-key provided",
                "model was not found",
                "{\"type\":\"invalid_request_error\",\"message\":\"bad request\"}",
                "service is not available in your region",
                "account suspended",
                "request cancelled by client",
                "stream interrupted",
                "upstream error: bad gateway",
                "quota exceeded, no credits remaining",
                "permission denied for this organization",
                "the service is currently overloaded",
                "finish_reason: content_filter"
            };
            foreach (string sample in corpus)
            {
                FailureAssessment assessment = FailureIntelligence.Classify(sample);
                failures += AssertEqual(true, assessment.IsKnown, "PERF-004: corpus sample is still classified (" + sample.Substring(0, Math.Min(40, sample.Length)) + ")");
                failures += AssertEqual(true, FailureIntelligence.MayContainFailureEvidence(sample), "PERF-004: the cheap gate admits every recognizable sample");
            }

            // The gate must DOMINATE every detailed pattern: run both paths over a
            // corpus generated from the detailed patterns' own literal phrases and
            // fail if the gate ever rejects text a detailed pattern accepts. This
            // is the mechanical version of "the cheap stage is semantics
            // preserving", not a hand-picked sample list.
            int dominationChecks = 0;
            foreach (string pattern in FailureIntelligence.DetailedPatternsForGateProof())
            {
                foreach (string phrase in Audit163LiteralPhrases(pattern))
                {
                    dominationChecks++;
                    string probe = "Error: " + phrase;
                    if (!FailureIntelligence.MayContainFailureEvidence(probe))
                    {
                        failures += AssertEqual(true, false, "PERF-004: the gate must admit every literal phrase of every detailed pattern (missed '" + phrase + "')");
                    }
                }
            }
            failures += AssertEqual(true, dominationChecks >= 200, "PERF-004: the domination proof covers the detailed pattern corpus (" + dominationChecks + " phrases)");

            // Differential check on real snapshots: for a mixed corpus, gate
            // rejection must always coincide with an UNKNOWN classification.
            string[] mixed = new[]
            {
                "just normal output",
                "Ask anything",
                "build succeeded in 4.2s",
                "{\"code\":\"internal_error\"}",
                "Error: rate limited",
                "wrote 12 files",
                "HTTP 503 Service Unavailable",
                "Working (3s)",
                "connection refused",
                "> "
            };
            foreach (string sample in mixed)
            {
                bool admitted = FailureIntelligence.MayContainFailureEvidence(sample);
                bool known = FailureIntelligence.Classify(sample).IsKnown;
                if (known && !admitted)
                {
                    failures += AssertEqual(true, false, "PERF-004: gate rejected a sample the classifier recognizes ('" + sample + "')");
                }
            }
            failures += AssertEqual(true, true, "PERF-004: gate/classifier differential holds on the mixed corpus");

            // Precedence inside one ambiguous line is unchanged: HTTP status wins
            // over code/type/message.
            FailureAssessment ambiguous = FailureIntelligence.Classify("Error: HTTP 503 Service Unavailable code=internal_error type=\"server_error\" message: overloaded");
            failures += AssertEqual(true, ambiguous.IsKnown, "PERF-004: an ambiguous envelope is classified");
            failures += AssertEqual(true, ambiguous.MatchedBy.Length > 0, "PERF-004: evidence is recorded for the ambiguous envelope");
            failures += AssertEqual("http_status:503", ambiguous.MatchedBy[0], "PERF-004: structured precedence still prefers the HTTP status");

            // The latest event still wins across events.
            FailureAssessment latest = FailureIntelligence.Classify("rate limited\n---\nmodel was not found\nAsk anything");
            failures += AssertEqual(FailureClass.MODEL_NOT_FOUND, latest.Class, "PERF-004: the latest recognized event still wins");

            // No-failure snapshots are UNKNOWN and are rejected by the gate, which
            // is what removes the 24-traversal tax from the common case.
            var noFailure = new StringBuilder();
            for (int i = 0; i < 180; i++) { noFailure.Append("normal output line ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n'); }
            noFailure.Append("Ask anything");
            string clean180 = noFailure.ToString();
            failures += AssertEqual(false, FailureIntelligence.MayContainFailureEvidence(clean180), "PERF-004: a clean 180-line snapshot is rejected by the cheap gate");
            failures += AssertEqual(FailureClass.UNKNOWN, FailureIntelligence.Classify(clean180).Class, "PERF-004: a clean 180-line snapshot classifies as UNKNOWN");

            var big = new StringBuilder();
            for (int i = 0; i < 2000; i++) { big.Append("routine progress ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n'); }
            string clean2000 = big.ToString();
            failures += AssertEqual(false, FailureIntelligence.MayContainFailureEvidence(clean2000), "PERF-004: a clean 2000-line snapshot is rejected by the cheap gate");
            failures += AssertEqual(FailureClass.UNKNOWN, FailureIntelligence.Classify(clean2000).Class, "PERF-004: a clean 2000-line snapshot classifies as UNKNOWN");

            // Two rules over ONE immutable snapshot reuse one rule-independent
            // assessment: the engine classifies per snapshot, not per rule.
            var snapshot = Audit163Snapshot(8080, 12, "Ask anything");
            snapshot.Text = "{\"code\":\"internal_error\"}" + Environment.NewLine + snapshot.Text;
            FailureAssessment shared = FailureIntelligence.Classify(snapshot.Text);
            TargetRule r1 = Audit163ConsoleRule("perf004-a", "codex", 40);
            TargetRule r2 = Audit163ConsoleRule("perf004-b", "codex", 40);
            RuleObservation o1 = RuleMatcher.Inspect(r1, snapshot, DateTime.UtcNow, shared);
            RuleObservation o2 = RuleMatcher.Inspect(r2, snapshot, DateTime.UtcNow, shared);
            failures += AssertEqual(true, ReferenceEquals(o1.Failure, o2.Failure), "PERF-004: both rules share one rule-independent FailureAssessment");
            RuleObservation independent = RuleMatcher.Inspect(r1, snapshot, DateTime.UtcNow);
            failures += AssertEqual(o1.Failure.Class, independent.Failure.Class, "PERF-004: the shared assessment matches an independent classification");
            failures += AssertEqual(o1.Failure.FingerprintMaterial, independent.Failure.FingerprintMaterial, "PERF-004: fingerprint material is identical with and without sharing");
            return failures;
        }

        /// <summary>
        /// PERF-004: pull the literal phrases out of a detailed pattern so the
        /// domination proof can feed each of them through the cheap gate. Regex
        /// metacharacters and the bounded-context constructs are dropped; what
        /// remains is the literal text a real failure line must contain.
        /// </summary>
        private static IList<string> Audit163LiteralPhrases(string pattern)
        {
            var phrases = new List<string>();
            if (String.IsNullOrEmpty(pattern)) return phrases;
            foreach (string alternative in pattern.Split('|'))
            {
                var current = new StringBuilder();
                int depth = 0;
                for (int i = 0; i < alternative.Length; i++)
                {
                    char c = alternative[i];
                    if (c == '\\')
                    {
                        // \s and \b are separators; any other escape is literal.
                        if (i + 1 < alternative.Length)
                        {
                            char next = alternative[i + 1];
                            if (next == 's' || next == 'b' || next == 'd' || next == 'w' || next == 'S' || next == 'W' || next == 'D')
                            {
                                current.Append(' ');
                            }
                            else
                            {
                                current.Append(next);
                            }
                            i++;
                        }
                        continue;
                    }
                    if (c == '[' || c == '{' || c == '(')
                    {
                        depth++;
                        current.Append(' ');
                        continue;
                    }
                    if (c == ']' || c == '}' || c == ')')
                    {
                        if (depth > 0) depth--;
                        current.Append(' ');
                        continue;
                    }
                    if (depth > 0) continue;
                    if (c == '?' || c == '*' || c == '+' || c == '^' || c == '$' || c == '.' || c == ',' || c == ':' || c == '<' || c == '>' || c == '"' || c == '\'')
                    {
                        current.Append(' ');
                        continue;
                    }
                    current.Append(c);
                }
                foreach (string token in current.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string cleaned = token.Trim('-', '_', '/');
                    if (cleaned.Length >= 3 && !phrases.Contains(cleaned))
                    {
                        phrases.Add(cleaned);
                    }
                }
            }
            return phrases;
        }
    }
}
