using System;
using System.Collections.Generic;
using System.Linq;

namespace Ushell.Editor
{
    public enum UshellTaskStatus
    {
        Active,
        StepReached,
        Completed,
        Cancelled,
        TimedOut
    }

    public enum UshellTaskCompletionReason
    {
        None,
        Keyword,
        Manual,
        Step,
        Cancel,
        Timeout,
        DomainReload
    }

    [Serializable]
    public sealed class UshellTaskButton
    {
        public string Id { get; internal set; }
        public string Label { get; internal set; }
        public string Description { get; internal set; }
        public string Expression { get; internal set; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "buttonId", Id },
                { "label", Label },
                { "description", Description },
                { "expression", Expression }
            };
        }
    }

    [Serializable]
    public sealed class UshellTaskAutoTrigger
    {
        public string Keyword { get; internal set; }
        public string Description { get; internal set; }
        public string Expression { get; internal set; }
        public bool Confirm { get; internal set; }
        public bool HasFired { get; internal set; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "keyword", Keyword },
                { "description", Description },
                { "expression", Expression },
                { "confirm", Confirm },
                { "hasFired", HasFired }
            };
        }
    }

    [Serializable]
    public sealed class UshellTaskStep
    {
        public string Id { get; internal set; }
        public string Keyword { get; internal set; }
        public string Description { get; internal set; }
        public bool HasReached { get; internal set; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "stepId", Id },
                { "keyword", Keyword },
                { "description", Description },
                { "hasReached", HasReached }
            };
        }
    }

    [Serializable]
    public sealed class UshellTaskAutoTriggerInvocation
    {
        public string Keyword { get; internal set; }
        public string Description { get; internal set; }
        public string Expression { get; internal set; }
        public bool Confirm { get; internal set; }
        public bool Success { get; internal set; }
        public object ReturnValue { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public long DurationMs { get; internal set; }
        public string TimestampUtc { get; internal set; }
        public List<Dictionary<string, object>> CapturedLogs { get; internal set; } = new List<Dictionary<string, object>>();

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "keyword", Keyword },
                { "description", Description },
                { "expression", Expression },
                { "confirm", Confirm },
                { "success", Success },
                { "returnValue", ReturnValue },
                { "errorMessage", ErrorMessage },
                { "durationMs", DurationMs },
                { "timestampUtc", TimestampUtc },
                { "capturedLogs", CapturedLogs.Select(CloneDictionary).ToList() }
            };
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source);
        }
    }

    [Serializable]
    public sealed class UshellTaskButtonInvocation
    {
        public string ButtonId { get; internal set; }
        public string Label { get; internal set; }
        public string Expression { get; internal set; }
        public bool Success { get; internal set; }
        public object ReturnValue { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public long DurationMs { get; internal set; }
        public string TimestampUtc { get; internal set; }
        public List<Dictionary<string, object>> CapturedLogs { get; internal set; } = new List<Dictionary<string, object>>();

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "buttonId", ButtonId },
                { "label", Label },
                { "expression", Expression },
                { "success", Success },
                { "returnValue", ReturnValue },
                { "errorMessage", ErrorMessage },
                { "durationMs", DurationMs },
                { "timestampUtc", TimestampUtc },
                { "capturedLogs", CapturedLogs.Select(CloneDictionary).ToList() }
            };
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source);
        }
    }

    [Serializable]
    public sealed class UshellTaskRecord
    {
        public string TaskId { get; internal set; }
        public string Description { get; internal set; }
        public string LogKeyword { get; internal set; }
        public string CompletionKeyword { get; internal set; }
        public UshellTaskStatus Status { get; internal set; }
        public UshellTaskCompletionReason Reason { get; internal set; }
        public string CreatedAtUtc { get; internal set; }
        public string EndedAtUtc { get; internal set; }
        public long LastSeenSequence { get; internal set; }
        public List<UshellTaskButton> Buttons { get; internal set; } = new List<UshellTaskButton>();
        public List<UshellTaskAutoTrigger> AutoTriggers { get; internal set; } = new List<UshellTaskAutoTrigger>();
        public List<UshellTaskStep> Steps { get; internal set; } = new List<UshellTaskStep>();
        public UshellTaskStep ReachedStep { get; internal set; }
        public List<Dictionary<string, object>> CapturedLogs { get; internal set; } = new List<Dictionary<string, object>>();
        public List<UshellTaskButtonInvocation> ButtonInvocations { get; internal set; } = new List<UshellTaskButtonInvocation>();
        public List<UshellTaskAutoTriggerInvocation> AutoTriggerInvocations { get; internal set; } = new List<UshellTaskAutoTriggerInvocation>();

        public UshellTaskAutoTrigger AutoTrigger => AutoTriggers.FirstOrDefault();

        internal UshellTaskRecord Clone()
        {
            return new UshellTaskRecord
            {
                TaskId = TaskId,
                Description = Description,
                LogKeyword = LogKeyword,
                CompletionKeyword = CompletionKeyword,
                Status = Status,
                Reason = Reason,
                CreatedAtUtc = CreatedAtUtc,
                EndedAtUtc = EndedAtUtc,
                LastSeenSequence = LastSeenSequence,
                Buttons = Buttons.Select(CloneButton).ToList(),
                AutoTriggers = AutoTriggers.Select(CloneAutoTrigger).ToList(),
                Steps = Steps.Select(CloneStep).ToList(),
                ReachedStep = CloneStep(ReachedStep),
                CapturedLogs = CapturedLogs.Select(CloneDictionary).ToList(),
                ButtonInvocations = ButtonInvocations.Select(CloneInvocation).ToList(),
                AutoTriggerInvocations = AutoTriggerInvocations.Select(CloneAutoTriggerInvocation).ToList()
            };
        }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "taskId", TaskId },
                { "status", FormatStatus(Status) },
                { "reason", FormatReason(Reason) },
                { "description", Description },
                { "logKeyword", LogKeyword },
                { "completionKeyword", CompletionKeyword },
                { "buttons", Buttons.Select(button => button.ToDictionary()).ToList() },
                { "autoTrigger", AutoTrigger == null ? null : AutoTrigger.ToDictionary() },
                { "autoTriggers", AutoTriggers.Select(trigger => trigger.ToDictionary()).ToList() },
                { "steps", Steps.Select(step => step.ToDictionary()).ToList() },
                { "reachedStep", ReachedStep == null ? null : ReachedStep.ToDictionary() },
                { "capturedLogs", CapturedLogs.Select(CloneDictionary).ToList() },
                { "buttonInvocations", ButtonInvocations.Select(invocation => invocation.ToDictionary()).ToList() },
                { "autoTriggerInvocations", AutoTriggerInvocations.Select(invocation => invocation.ToDictionary()).ToList() },
                { "createdAtUtc", CreatedAtUtc },
                { "endedAtUtc", EndedAtUtc }
            };
        }

        public Dictionary<string, object> ToSummaryDictionary()
        {
            return new Dictionary<string, object>
            {
                { "taskId", TaskId },
                { "status", FormatStatus(Status) },
                { "reason", FormatReason(Reason) },
                { "description", Description },
                { "createdAtUtc", CreatedAtUtc },
                { "endedAtUtc", EndedAtUtc },
                { "capturedLogCount", CapturedLogs.Count },
                { "autoTriggerInvocationCount", AutoTriggerInvocations.Count },
                { "reachedStep", ReachedStep == null ? null : ReachedStep.ToDictionary() }
            };
        }

        public static string FormatStatus(UshellTaskStatus status)
        {
            switch (status)
            {
                case UshellTaskStatus.StepReached:
                    return "step_reached";
                case UshellTaskStatus.Completed:
                    return "completed";
                case UshellTaskStatus.Cancelled:
                    return "cancelled";
                case UshellTaskStatus.TimedOut:
                    return "timed_out";
                default:
                    return "active";
            }
        }

        public static string FormatReason(UshellTaskCompletionReason reason)
        {
            switch (reason)
            {
                case UshellTaskCompletionReason.Keyword:
                    return "keyword";
                case UshellTaskCompletionReason.Manual:
                    return "manual";
                case UshellTaskCompletionReason.Step:
                    return "step";
                case UshellTaskCompletionReason.Cancel:
                    return "cancel";
                case UshellTaskCompletionReason.Timeout:
                    return "timeout";
                case UshellTaskCompletionReason.DomainReload:
                    return "domain_reload";
                default:
                    return null;
            }
        }

        private static UshellTaskButton CloneButton(UshellTaskButton source)
        {
            return new UshellTaskButton
            {
                Id = source.Id,
                Label = source.Label,
                Description = source.Description,
                Expression = source.Expression
            };
        }

        private static UshellTaskButtonInvocation CloneInvocation(UshellTaskButtonInvocation source)
        {
            return new UshellTaskButtonInvocation
            {
                ButtonId = source.ButtonId,
                Label = source.Label,
                Expression = source.Expression,
                Success = source.Success,
                ReturnValue = source.ReturnValue,
                ErrorMessage = source.ErrorMessage,
                DurationMs = source.DurationMs,
                TimestampUtc = source.TimestampUtc,
                CapturedLogs = source.CapturedLogs.Select(CloneDictionary).ToList()
            };
        }

        private static UshellTaskAutoTrigger CloneAutoTrigger(UshellTaskAutoTrigger source)
        {
            if (source == null)
            {
                return null;
            }

            return new UshellTaskAutoTrigger
            {
                Keyword = source.Keyword,
                Description = source.Description,
                Expression = source.Expression,
                Confirm = source.Confirm,
                HasFired = source.HasFired
            };
        }

        private static UshellTaskStep CloneStep(UshellTaskStep source)
        {
            if (source == null)
            {
                return null;
            }

            return new UshellTaskStep
            {
                Id = source.Id,
                Keyword = source.Keyword,
                Description = source.Description,
                HasReached = source.HasReached
            };
        }

        private static UshellTaskAutoTriggerInvocation CloneAutoTriggerInvocation(UshellTaskAutoTriggerInvocation source)
        {
            return new UshellTaskAutoTriggerInvocation
            {
                Keyword = source.Keyword,
                Description = source.Description,
                Expression = source.Expression,
                Confirm = source.Confirm,
                Success = source.Success,
                ReturnValue = source.ReturnValue,
                ErrorMessage = source.ErrorMessage,
                DurationMs = source.DurationMs,
                TimestampUtc = source.TimestampUtc,
                CapturedLogs = source.CapturedLogs.Select(CloneDictionary).ToList()
            };
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source);
        }
    }
}
