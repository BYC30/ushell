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
            foreach (UshellTaskRecord record in UshellTaskPersistence.Restore())
            {
                States.Add(new TaskState { Record = record });
            }

            EditorApplication.update += PollActiveTasks;
            AssemblyReloadEvents.beforeAssemblyReload += PersistForDomainReload;
            EditorApplication.quitting += CancelActiveForEditorQuit;
        }

        public static string CreateTask(
            string description,
            string logKeyword,
            string completionKeyword,
            IReadOnlyList<UshellTaskButton> buttons,
            IReadOnlyList<UshellTaskAutoTrigger> autoTriggers,
            IReadOnlyList<UshellTaskStep> steps)
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
                    AutoTriggers = NormalizeAutoTriggers(autoTriggers),
                    Steps = NormalizeSteps(steps)
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

        public static string ContinueTask(
            string taskId,
            string description,
            string logKeyword,
            string completionKeyword,
            IReadOnlyList<UshellTaskButton> buttons,
            IReadOnlyList<UshellTaskAutoTrigger> autoTriggers,
            IReadOnlyList<UshellTaskStep> steps)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new InvalidOperationException("Argument 'taskId' is required.");
            }

            if (string.IsNullOrWhiteSpace(logKeyword))
            {
                throw new InvalidOperationException("Argument 'logKeyword' is required.");
            }

            if (string.IsNullOrWhiteSpace(completionKeyword))
            {
                throw new InvalidOperationException("Argument 'completionKeyword' is required.");
            }

            long currentLogSequence = GetCurrentLogSequence();
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null)
                {
                    throw new InvalidOperationException($"Unknown task '{taskId}'.");
                }

                if (state.Record.Status != UshellTaskStatus.StepReached)
                {
                    throw new InvalidOperationException("Only a task in step_reached state can be continued.");
                }

                if (!string.IsNullOrWhiteSpace(description))
                {
                    state.Record.Description = description.Trim();
                }

                state.Record.LogKeyword = logKeyword.Trim();
                state.Record.CompletionKeyword = completionKeyword.Trim();
                state.Record.LastSeenSequence = currentLogSequence;
                state.Record.Buttons = NormalizeButtons(buttons);
                state.Record.AutoTriggers = NormalizeAutoTriggers(autoTriggers);
                state.Record.Steps = NormalizeSteps(steps);
                state.Record.ReachedStep = null;
                state.Record.Status = UshellTaskStatus.Active;
                state.Record.Reason = UshellTaskCompletionReason.None;
                state.Record.EndedAtUtc = null;
                ScheduleChangedNotificationLocked();
            }

            UshellTaskWindow.ShowWindow();
            return taskId;
        }

        public static void MarkCompletedManual(string taskId)
        {
            CompleteAfterFinalLogPoll(taskId, UshellTaskStatus.Completed, UshellTaskCompletionReason.Manual);
        }

        public static void Cancel(string taskId)
        {
            CompleteAfterFinalLogPoll(taskId, UshellTaskStatus.Cancelled, UshellTaskCompletionReason.Cancel);
        }

        public static void MarkTimedOut(string taskId)
        {
            CompleteAfterFinalLogPoll(taskId, UshellTaskStatus.TimedOut, UshellTaskCompletionReason.Timeout);
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

        private static bool PollTaskLogs(string taskId)
        {
            string logKeyword;
            string completionKeyword;
            List<KeyValuePair<int, UshellTaskAutoTrigger>> armedAutoTriggers;
            List<UshellTaskStep> armedSteps;
            long lastSeenSequence;
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null || state.Record.Status != UshellTaskStatus.Active)
                {
                    return false;
                }

                logKeyword = state.Record.LogKeyword;
                completionKeyword = state.Record.CompletionKeyword;
                armedAutoTriggers = state.Record.AutoTriggers
                    .Select((trigger, index) => new KeyValuePair<int, UshellTaskAutoTrigger>(index, trigger))
                    .Where(pair => pair.Value != null && !pair.Value.HasFired)
                    .Select(pair => new KeyValuePair<int, UshellTaskAutoTrigger>(pair.Key, CloneAutoTrigger(pair.Value)))
                    .ToList();
                armedSteps = state.Record.Steps
                    .Where(step => step != null && !step.HasReached)
                    .Select(CloneStep)
                    .ToList();
                lastSeenSequence = state.Record.LastSeenSequence;
            }

            IReadOnlyList<Dictionary<string, object>> newEntries = UshellLogStore.GetEntries(null, lastSeenSequence, null, null, LogPollLimit);
            if (newEntries.Count == 0)
            {
                return false;
            }

            List<Dictionary<string, object>> matchedLogs = new List<Dictionary<string, object>>();
            bool matchedCompletion = false;
            List<int> matchedAutoTriggerIndexes = new List<int>();
            UshellTaskStep matchedStep = null;
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
            }

            matchedAutoTriggerIndexes.AddRange(armedAutoTriggers
                .Where(pair => newEntries.Any(entry => ContainsKeyword(entry, pair.Value.Keyword)))
                .Select(pair => pair.Key));
            matchedStep = armedSteps.FirstOrDefault(step => newEntries.Any(entry => ContainsKeyword(entry, step.Keyword)));

            List<UshellTaskAutoTrigger> autoTriggersToInvoke = new List<UshellTaskAutoTrigger>();
            bool completeAfterAutoTriggers = false;
            lock (SyncRoot)
            {
                TaskState state = FindStateLocked(taskId);
                if (state == null || state.Record.Status != UshellTaskStatus.Active)
                {
                    return false;
                }

                state.Record.LastSeenSequence = Math.Max(state.Record.LastSeenSequence, maxSequence);
                if (matchedLogs.Count > 0)
                {
                    state.Record.CapturedLogs.AddRange(matchedLogs);
                }

                if (matchedCompletion)
                {
                    MarkAutoTriggersFiredLocked(state, matchedAutoTriggerIndexes, autoTriggersToInvoke);
                    if (autoTriggersToInvoke.Count == 0)
                    {
                        CompleteLocked(state, UshellTaskStatus.Completed, UshellTaskCompletionReason.Keyword);
                    }
                    else
                    {
                        completeAfterAutoTriggers = true;
                        ScheduleChangedNotificationLocked();
                    }
                }
                else if (matchedStep != null)
                {
                    UshellTaskStep step = state.Record.Steps.FirstOrDefault(item =>
                        item != null && !item.HasReached && string.Equals(item.Id, matchedStep.Id, StringComparison.Ordinal));
                    if (step != null)
                    {
                        step.HasReached = true;
                        state.Record.ReachedStep = CloneStep(step);
                        CompleteLocked(state, UshellTaskStatus.StepReached, UshellTaskCompletionReason.Step);
                    }
                }
                else
                {
                    MarkAutoTriggersFiredLocked(state, matchedAutoTriggerIndexes, autoTriggersToInvoke);
                    if (matchedLogs.Count > 0 || autoTriggersToInvoke.Count > 0)
                    {
                        ScheduleChangedNotificationLocked();
                    }
                }
            }

            foreach (UshellTaskAutoTrigger autoTrigger in autoTriggersToInvoke)
            {
                UshellTaskAutoTriggerInvocation invocation = ExecuteAutoTrigger(autoTrigger);
                lock (SyncRoot)
                {
                    TaskState state = FindStateLocked(taskId);
                    if (state == null)
                    {
                        return true;
                    }

                    state.Record.AutoTriggerInvocations.Add(invocation);
                    ScheduleChangedNotificationLocked();
                }
            }

            if (completeAfterAutoTriggers)
            {
                Complete(taskId, UshellTaskStatus.Completed, UshellTaskCompletionReason.Keyword);
            }

            return true;
        }

        private static void MarkAutoTriggersFiredLocked(
            TaskState state,
            IReadOnlyList<int> matchedIndexes,
            ICollection<UshellTaskAutoTrigger> triggersToInvoke)
        {
            foreach (int index in matchedIndexes)
            {
                if (index < 0 || index >= state.Record.AutoTriggers.Count)
                {
                    continue;
                }

                UshellTaskAutoTrigger trigger = state.Record.AutoTriggers[index];
                if (trigger == null || trigger.HasFired)
                {
                    continue;
                }

                trigger.HasFired = true;
                triggersToInvoke.Add(CloneAutoTrigger(trigger));
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

        private static void CompleteAfterFinalLogPoll(string taskId, UshellTaskStatus status, UshellTaskCompletionReason reason)
        {
            for (int iteration = 0; iteration < 32 && PollTaskLogs(taskId); iteration++)
            {
                // Drain logs and any finite auto-trigger chain before committing the terminal state.
            }

            Complete(taskId, status, reason);
        }

        private static void CompleteLocked(TaskState state, UshellTaskStatus status, UshellTaskCompletionReason reason)
        {
            state.Record.Status = status;
            state.Record.Reason = reason;
            state.Record.EndedAtUtc = DateTime.UtcNow.ToString("O");
            ScheduleChangedNotificationLocked();
        }

        private static void PersistForDomainReload()
        {
            List<UshellTaskRecord> records;
            lock (SyncRoot)
            {
                records = States.Select(state => state.Record.Clone()).ToList();
            }

            UshellTaskPersistence.Save(records);
        }

        private static void CancelActiveForEditorQuit()
        {
            lock (SyncRoot)
            {
                foreach (TaskState state in States.Where(item => item.Record.Status == UshellTaskStatus.Active))
                {
                    CompleteLocked(state, UshellTaskStatus.Cancelled, UshellTaskCompletionReason.Cancel);
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

        private static List<UshellTaskAutoTrigger> NormalizeAutoTriggers(IReadOnlyList<UshellTaskAutoTrigger> autoTriggers)
        {
            List<UshellTaskAutoTrigger> normalized = new List<UshellTaskAutoTrigger>();
            if (autoTriggers == null)
            {
                return normalized;
            }

            foreach (UshellTaskAutoTrigger autoTrigger in autoTriggers)
            {
                if (autoTrigger == null || string.IsNullOrWhiteSpace(autoTrigger.Keyword) || string.IsNullOrWhiteSpace(autoTrigger.Expression))
                {
                    continue;
                }

                normalized.Add(new UshellTaskAutoTrigger
                {
                    Keyword = autoTrigger.Keyword.Trim(),
                    Description = string.IsNullOrWhiteSpace(autoTrigger.Description) ? null : autoTrigger.Description.Trim(),
                    Expression = autoTrigger.Expression.Trim(),
                    Confirm = autoTrigger.Confirm,
                    HasFired = false
                });
            }

            return normalized;
        }

        private static List<UshellTaskStep> NormalizeSteps(IReadOnlyList<UshellTaskStep> steps)
        {
            List<UshellTaskStep> normalized = new List<UshellTaskStep>();
            if (steps == null)
            {
                return normalized;
            }

            for (int index = 0; index < steps.Count; index++)
            {
                UshellTaskStep step = steps[index];
                if (step == null || string.IsNullOrWhiteSpace(step.Keyword))
                {
                    continue;
                }

                string stepId = string.IsNullOrWhiteSpace(step.Id) ? $"step-{index + 1}" : step.Id.Trim();
                if (normalized.Any(item => string.Equals(item.Id, stepId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"Duplicate task step id '{stepId}'.");
                }

                normalized.Add(new UshellTaskStep
                {
                    Id = stepId,
                    Keyword = step.Keyword.Trim(),
                    Description = string.IsNullOrWhiteSpace(step.Description) ? null : step.Description.Trim(),
                    HasReached = false
                });
            }

            return normalized;
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
