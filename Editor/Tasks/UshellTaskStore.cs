using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    [InitializeOnLoad]
    public static class UshellTaskStore
    {
        private sealed class TaskState
        {
            public UshellTaskRecord Record;
        }

        private const int LogPollLimit = 1000;
        private static readonly object SyncRoot = new object();
        private static readonly List<TaskState> States = new List<TaskState>();

        public static event Action OnChanged;

        static UshellTaskStore()
        {
            EditorApplication.update += PollActiveTasks;
            AssemblyReloadEvents.beforeAssemblyReload += CancelActiveForDomainReload;
            EditorApplication.quitting += CancelActiveForDomainReload;
        }

        public static string CreateTask(string description, string logKeyword, string completionKeyword, IReadOnlyList<UshellTaskButton> buttons, UshellTaskAutoTrigger autoTrigger)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                throw new InvalidOperationException("Argument 'description' is required.");
            }

            if (string.IsNullOrWhiteSpace(logKeyword))
            {
                throw new InvalidOperationException("Argument 'logKeyword' is required.");
            }

            if (string.IsNullOrWhiteSpace(completionKeyword))
            {
                throw new InvalidOperationException("Argument 'completionKeyword' is required.");
            }

            string taskId = Guid.NewGuid().ToString("N");
            TaskState state = new TaskState
            {
                Record = new UshellTaskRecord
                {
                    TaskId = taskId,
                    Description = description.Trim(),
                    LogKeyword = logKeyword.Trim(),
                    CompletionKeyword = completionKeyword.Trim(),
                    Status = UshellTaskStatus.Active,
                    Reason = UshellTaskCompletionReason.None,
                    CreatedAtUtc = DateTime.UtcNow.ToString("O"),
                    LastSeenSequence = GetCurrentLogSequence(),
                    Buttons = NormalizeButtons(buttons),
                    AutoTrigger = NormalizeAutoTrigger(autoTrigger)
                }
            };

            lock (SyncRoot)
            {
                States.Add(state);
                ScheduleChangedNotificationLocked();
            }

            UshellTaskWindow.ShowWindow();
            return taskId;
        }

        public static void MarkCompletedManual(string taskId)
        {
            Complete(taskId, UshellTaskStatus.Completed, UshellTaskCompletionReason.Manual);
        }

        public static void Cancel(string taskId)
        {
            Complete(taskId, UshellTaskStatus.Cancelled, UshellTaskCompletionReason.Cancel);
        }

        public static void MarkTimedOut(string taskId)
        {
            Complete(taskId, UshellTaskStatus.TimedOut, UshellTaskCompletionReason.Timeout);
        }

        public static UshellToolEnvelope InvokeButton(string taskId, string buttonId)
        {
            UshellTaskButton button;
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null)
                {
                    return UshellToolEnvelope.FromError("TASK_NOT_FOUND", $"Unknown task '{taskId}'.");
                }

                if (state.Record.Status != UshellTaskStatus.Active)
                {
                    return UshellToolEnvelope.FromError("TASK_NOT_ACTIVE", "Only active tasks can invoke buttons.");
                }

                button = state.Record.Buttons.FirstOrDefault(item => string.Equals(item.Id, buttonId, StringComparison.Ordinal));
                if (button == null)
                {
                    return UshellToolEnvelope.FromError("BUTTON_NOT_FOUND", $"Unknown button '{buttonId}'.");
                }
            }

            UshellSettings settings = UshellSettings.Instance;
            if (settings.DangerousOperationRequireConfirm)
            {
                string message = string.IsNullOrWhiteSpace(button.Description)
                    ? $"Execute task button '{button.Label}'?"
                    : $"{button.Description}\n\nExecute expression:\n{button.Expression}";
                if (!EditorUtility.DisplayDialog("Confirm Ushell Task Button", message, "Execute", "Cancel"))
                {
                    return UshellToolEnvelope.FromError("USER_CANCELLED", "The task button invocation was cancelled by the user.");
                }
            }

            UshellCodeExecutionResult result = UshellRoslynExecutor.Execute(button.Expression, true, settings.MaxExecutionSeconds * 1000);
            UshellTaskButtonInvocation invocation = new UshellTaskButtonInvocation
            {
                ButtonId = button.Id,
                Label = button.Label,
                Expression = button.Expression,
                Success = result.Success,
                ReturnValue = result.ReturnValue,
                ErrorMessage = result.Success ? null : result.ErrorMessage,
                DurationMs = result.DurationMs,
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                CapturedLogs = result.Logs.Select(CloneDictionary).ToList()
            };

            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null)
                {
                    return UshellToolEnvelope.FromError("TASK_NOT_FOUND", $"Unknown task '{taskId}'.");
                }

                state.Record.ButtonInvocations.Add(invocation);
                ScheduleChangedNotificationLocked();
            }

            if (!result.Success)
            {
                return UshellToolEnvelope.FromError(
                    result.ErrorCode,
                    result.ErrorMessage,
                    new Dictionary<string, object>
                    {
                        { "buttonInvocation", invocation.ToDictionary() },
                        { "execution", result.Details }
                    });
            }

            UshellToolEnvelope envelope = UshellToolEnvelope.FromSuccess(invocation.ToDictionary());
            envelope.Logs.AddRange(result.Logs);
            envelope.Warnings.AddRange(result.Warnings);
            return envelope;
        }

        public static List<UshellTaskRecord> Snapshot(bool includeCompleted, int limit)
        {
            lock (SyncRoot)
            {
                IEnumerable<TaskState> query = States;
                if (!includeCompleted)
                {
                    query = query.Where(state => state.Record.Status == UshellTaskStatus.Active);
                }

                return query
                    .OrderByDescending(state => state.Record.CreatedAtUtc, StringComparer.Ordinal)
                    .Take(Mathf.Clamp(limit, 1, 500))
                    .Select(state => state.Record.Clone())
                    .ToList();
            }
        }

        public static UshellTaskRecord GetTask(string taskId)
        {
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                return state == null ? null : state.Record.Clone();
            }
        }

        public static Dictionary<string, object> GetStatus(string taskId)
        {
            UshellTaskRecord record = GetTask(taskId);
            if (record != null)
            {
                return record.ToDictionary();
            }

            return new Dictionary<string, object>
            {
                { "taskId", taskId },
                { "status", "not_found" }
            };
        }

        public static bool HasActiveTasks()
        {
            lock (SyncRoot)
            {
                return States.Any(state => state.Record.Status == UshellTaskStatus.Active);
            }
        }

        private static void PollActiveTasks()
        {
            List<TaskState> activeStates;
            lock (SyncRoot)
            {
                activeStates = States
                    .Where(state => state.Record.Status == UshellTaskStatus.Active)
                    .ToList();
            }

            foreach (TaskState state in activeStates)
            {
                PollTaskLogs(state.Record.TaskId);
            }
        }

        private static void PollTaskLogs(string taskId)
        {
            string logKeyword;
            string completionKeyword;
            string autoTriggerKeyword;
            long lastSeenSequence;
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null || state.Record.Status != UshellTaskStatus.Active)
                {
                    return;
                }

                logKeyword = state.Record.LogKeyword;
                completionKeyword = state.Record.CompletionKeyword;
                autoTriggerKeyword = state.Record.AutoTrigger != null && !state.Record.AutoTrigger.HasFired
                    ? state.Record.AutoTrigger.Keyword
                    : null;
                lastSeenSequence = state.Record.LastSeenSequence;
            }

            IReadOnlyList<Dictionary<string, object>> newEntries = UshellLogStore.GetEntries(null, lastSeenSequence, null, null, LogPollLimit);
            if (newEntries.Count == 0)
            {
                return;
            }

            List<Dictionary<string, object>> matchedLogs = new List<Dictionary<string, object>>();
            bool matchedCompletion = false;
            bool matchedAutoTrigger = false;
            long maxSequence = lastSeenSequence;
            foreach (Dictionary<string, object> entry in newEntries)
            {
                long sequence = ReadSequence(entry);
                if (sequence > maxSequence)
                {
                    maxSequence = sequence;
                }

                if (ContainsKeyword(entry, logKeyword))
                {
                    matchedLogs.Add(CloneDictionary(entry));
                }

                if (ContainsKeyword(entry, completionKeyword))
                {
                    matchedCompletion = true;
                }

                if (ContainsKeyword(entry, autoTriggerKeyword))
                {
                    matchedAutoTrigger = true;
                }
            }

            UshellTaskAutoTrigger autoTriggerToInvoke = null;
            bool completeAfterAutoTrigger = false;
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null || state.Record.Status != UshellTaskStatus.Active)
                {
                    return;
                }

                state.Record.LastSeenSequence = Math.Max(state.Record.LastSeenSequence, maxSequence);
                if (matchedLogs.Count > 0)
                {
                    state.Record.CapturedLogs.AddRange(matchedLogs);
                }

                if (matchedAutoTrigger && state.Record.AutoTrigger != null && !state.Record.AutoTrigger.HasFired)
                {
                    state.Record.AutoTrigger.HasFired = true;
                    autoTriggerToInvoke = CloneAutoTrigger(state.Record.AutoTrigger);
                    completeAfterAutoTrigger = matchedCompletion;
                    ScheduleChangedNotificationLocked();
                }
                else if (matchedCompletion)
                {
                    CompleteLocked(state, UshellTaskStatus.Completed, UshellTaskCompletionReason.Keyword);
                }
                else if (matchedLogs.Count > 0)
                {
                    ScheduleChangedNotificationLocked();
                }
            }

            if (autoTriggerToInvoke != null)
            {
                UshellTaskAutoTriggerInvocation invocation = ExecuteAutoTrigger(autoTriggerToInvoke);
                lock (SyncRoot)
                {
                    TaskState state = FindStateLocked(taskId);
                    if (state == null)
                    {
                        return;
                    }

                    state.Record.AutoTriggerInvocations.Add(invocation);
                    if (completeAfterAutoTrigger && state.Record.Status == UshellTaskStatus.Active)
                    {
                        CompleteLocked(state, UshellTaskStatus.Completed, UshellTaskCompletionReason.Keyword);
                    }
                    else
                    {
                        ScheduleChangedNotificationLocked();
                    }
                }
            }
        }

        private static void Complete(string taskId, UshellTaskStatus status, UshellTaskCompletionReason reason)
        {
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null || state.Record.Status != UshellTaskStatus.Active)
                {
                    return;
                }

                CompleteLocked(state, status, reason);
            }
        }

        private static void CompleteLocked(TaskState state, UshellTaskStatus status, UshellTaskCompletionReason reason)
        {
            state.Record.Status = status;
            state.Record.Reason = reason;
            state.Record.EndedAtUtc = DateTime.UtcNow.ToString("O");
            ScheduleChangedNotificationLocked();
        }

        public static void CancelActiveForDomainReload()
        {
            lock (SyncRoot)
            {
                foreach (TaskState state in States)
                {
                    if (state.Record.Status == UshellTaskStatus.Active)
                    {
                        CompleteLocked(state, UshellTaskStatus.Cancelled, UshellTaskCompletionReason.DomainReload);
                    }
                }
            }
        }

        private static TaskState FindStateLocked(string taskId)
        {
            return States.FirstOrDefault(state => string.Equals(state.Record.TaskId, taskId, StringComparison.Ordinal));
        }

        private static List<UshellTaskButton> NormalizeButtons(IReadOnlyList<UshellTaskButton> buttons)
        {
            List<UshellTaskButton> normalized = new List<UshellTaskButton>();
            if (buttons == null)
            {
                return normalized;
            }

            for (int index = 0; index < buttons.Count; index++)
            {
                UshellTaskButton button = buttons[index];
                if (button == null || string.IsNullOrWhiteSpace(button.Label) || string.IsNullOrWhiteSpace(button.Expression))
                {
                    continue;
                }

                normalized.Add(new UshellTaskButton
                {
                    Id = string.IsNullOrWhiteSpace(button.Id) ? $"button-{index + 1}" : button.Id,
                    Label = button.Label.Trim(),
                    Description = string.IsNullOrWhiteSpace(button.Description) ? null : button.Description.Trim(),
                    Expression = button.Expression.Trim()
                });
            }

            return normalized;
        }

        private static UshellTaskAutoTrigger NormalizeAutoTrigger(UshellTaskAutoTrigger autoTrigger)
        {
            if (autoTrigger == null || string.IsNullOrWhiteSpace(autoTrigger.Keyword) || string.IsNullOrWhiteSpace(autoTrigger.Expression))
            {
                return null;
            }

            return new UshellTaskAutoTrigger
            {
                Keyword = autoTrigger.Keyword.Trim(),
                Description = string.IsNullOrWhiteSpace(autoTrigger.Description) ? null : autoTrigger.Description.Trim(),
                Expression = autoTrigger.Expression.Trim(),
                Confirm = autoTrigger.Confirm,
                HasFired = false
            };
        }

        private static UshellTaskAutoTriggerInvocation ExecuteAutoTrigger(UshellTaskAutoTrigger autoTrigger)
        {
            UshellSettings settings = UshellSettings.Instance;
            if (settings.DangerousOperationRequireConfirm && !autoTrigger.Confirm)
            {
                return new UshellTaskAutoTriggerInvocation
                {
                    Keyword = autoTrigger.Keyword,
                    Description = autoTrigger.Description,
                    Expression = autoTrigger.Expression,
                    Confirm = autoTrigger.Confirm,
                    Success = false,
                    ErrorMessage = "autoTrigger requires confirm=true when dangerous-operation confirmation is enabled.",
                    DurationMs = 0,
                    TimestampUtc = DateTime.UtcNow.ToString("O")
                };
            }

            UshellCodeExecutionResult result = UshellRoslynExecutor.Execute(autoTrigger.Expression, true, settings.MaxExecutionSeconds * 1000);
            return new UshellTaskAutoTriggerInvocation
            {
                Keyword = autoTrigger.Keyword,
                Description = autoTrigger.Description,
                Expression = autoTrigger.Expression,
                Confirm = autoTrigger.Confirm,
                Success = result.Success,
                ReturnValue = result.ReturnValue,
                ErrorMessage = result.Success ? null : result.ErrorMessage,
                DurationMs = result.DurationMs,
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                CapturedLogs = result.Logs.Select(CloneDictionary).ToList()
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

        private static long GetCurrentLogSequence()
        {
            IReadOnlyList<Dictionary<string, object>> entries = UshellLogStore.GetEntries(null, null, null, null, 1);
            if (entries.Count == 0)
            {
                return 0;
            }

            return ReadSequence(entries[entries.Count - 1]);
        }

        private static bool ContainsKeyword(Dictionary<string, object> entry, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return false;
            }

            return Contains(ReadString(entry, "message"), keyword) || Contains(ReadString(entry, "stackTrace"), keyword);
        }

        private static bool Contains(string source, string keyword)
        {
            return !string.IsNullOrEmpty(source)
                && source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static long ReadSequence(Dictionary<string, object> entry)
        {
            if (entry == null || !entry.TryGetValue("sequence", out object value) || value == null)
            {
                return 0;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            long.TryParse(value.ToString(), out long parsed);
            return parsed;
        }

        private static string ReadString(Dictionary<string, object> entry, string key)
        {
            if (entry == null || !entry.TryGetValue(key, out object value))
            {
                return null;
            }

            return value?.ToString();
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source);
        }

        private static void ScheduleChangedNotificationLocked()
        {
            EditorApplication.delayCall -= NotifyChanged;
            EditorApplication.delayCall += NotifyChanged;
        }

        private static void NotifyChanged()
        {
            Action handler = OnChanged;
            if (handler == null)
            {
                return;
            }

            foreach (Action subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
