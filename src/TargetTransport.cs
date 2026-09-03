using System;
using System.Collections.Generic;

namespace SaiCont
{
    // SRC-022: normalized observation returned by any target transport. The
    // console transport maps the existing ConsoleSnapshot into this shape; the
    // opencode-http transport produces it directly from /event and /session
    // endpoints. RetryPolicy/FailureIntelligence/WatcherEngine consume only
    // this contract, never the raw transport-specific payload.
    internal sealed class TargetObservation
    {
        public string TargetName;
        public string TransportKind;

        public string EndpointIdentity;
        public string ProjectIdentity;
        public string SessionIdentity;
        public string ActiveAgent;
        public string ModelProvider;
        public string ModelId;
        public string TriggerToken;
        public DateTime RetryAtUtc;
        public string FailureCode;

        public string Text;
        public int CursorRow;
        public int StartRow;

        public bool Ready;
        public bool Busy;
        public bool CanSend;
        public string BlockReason;
        public string Diagnostic;
    }

    // SRC-022: minimum surface the engine needs from any transport. Three
    // methods keeps the seam boring: poll returns the latest observation;
    // validate checks transport-level safety (server reachability, identity
    // match) before send; send commits the continuation command exactly to
    // the session that produced the trigger.
    internal interface ITargetTransport
    {
        string Kind { get; }

        bool IsApplicable(TargetRule rule);

        TargetObservation Poll(TargetRule rule, DateTime nowUtc, out string error);

        bool ValidatePreSend(TargetObservation observation, TargetRule rule, out string error);

        SendOutcome Send(TargetObservation observation, TargetRule rule, string command, Func<bool> commitAllowed, out string error);

        bool TryTakeCompletedSend(string sessionIdentity, out SendOutcome outcome);

        bool HasPendingSend { get; }

        void Dispose();
    }

    internal enum SendDisposition
    {
        Unknown = 0,
        CompleteInputCommitted = 1,
        NoInputCommitted = 2,
        AmbiguousOrPartialInput = 3,
        Pending = 4
    }

    internal sealed class SendOutcome
    {
        public SendDisposition Disposition;
        public string ConfirmationToken;
        public string IdentityChecked;
    }
}
