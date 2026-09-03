using System;

namespace SaiCont
{
    internal static class WatcherRuleLimits
    {
        public const int PollIntervalMillisecondsMin = 250;
        public const int PollIntervalMillisecondsMax = 3600000;

        public const int MaximumScanLines = 4096;

        public const int ScanLinesMin = 10;
        public const int MaximumTriggerDistanceLinesMin = 1;
        public const int MaximumTriggerDistanceLinesMax = 5000;

        public const int InitialDelaySecondsMin = 10;
        public const int InitialDelaySecondsMax = 86400;

        public const int RetryIntervalSecondsMin = 10;
        public const int RetryIntervalSecondsMax = 86400;

        public const int MaxRetryIntervalSecondsMin = 10;
        public const int MaxRetryIntervalSecondsMax = 86400;
        public const int MaxRetryIntervalSecondsDefault = 3600;

        public const int MaxAttemptsMin = 1;
        public const int MaxAttemptsMax = 50;
        public const int MaxAttemptsDefault = 5;

        public const int LogMaximumBytesMin = 4096;
        public const int LogMaximumBytesMax = int.MaxValue;

        public const int LogRetainedFilesMin = 1;
        public const int LogRetainedFilesMax = 20;

        public const int LogDuplicateWindowSecondsMin = 1;
        public const int LogDuplicateWindowSecondsMax = 3600;

        public const double BackoffMultiplierMin = 1.0;
        public const double BackoffMultiplierMax = 10.0;
        public const double BackoffMultiplierDefault = 2.0;

        public const int PatternMaxLength = 2048;

        public const int TriggerPatternsMax = 32;
        public const int ReadyPatternsMax = 16;
        public const int BusyPatternsMax = 16;

        public const int CommandMaxLength = 512;
    }
}
