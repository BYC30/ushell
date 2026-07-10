using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Ushell.Editor
{
    internal static class UshellTaskPersistence
    {
        private const int Version = 1;

        private static string StorageKey => "ushell.tasks.reload." + UshellPaths.ProjectKey;

        public static void Save(IReadOnlyList<UshellTaskRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                SessionState.EraseString(StorageKey);
                return;
            }

            List<Dictionary<string, object>> tasks = records.Select(ToPersistenceDictionary).ToList();
            SessionState.SetString(StorageKey, MiniJson.Serialize(new Dictionary<string, object>
            {
                { "version", Version },
                { "tasks", tasks }
            }));
        }

        public static List<UshellTaskRecord> Restore()
        {
            string json = SessionState.GetString(StorageKey, null);
            SessionState.EraseString(StorageKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<UshellTaskRecord>();
            }

            try
            {
                Dictionary<string, object> root = MiniJson.Deserialize(json) as Dictionary<string, object>;
                return ReadObjectList(root, "tasks")
                    .Select(value => FromPersistenceDictionary(value as Dictionary<string, object>))
                    .Where(record => record != null && !string.IsNullOrWhiteSpace(record.TaskId))
                    .ToList();
            }
            catch
            {
                return new List<UshellTaskRecord>();
            }
        }

        private static Dictionary<string, object> ToPersistenceDictionary(UshellTaskRecord record)
        {
            Dictionary<string, object> data = record.ToDictionary();
            data["lastSeenSequence"] = record.LastSeenSequence;
            return data;
        }

        private static UshellTaskRecord FromPersistenceDictionary(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            List<UshellTaskAutoTrigger> autoTriggers = ReadObjectList(data, "autoTriggers")
                .Select(value => ReadAutoTrigger(value as Dictionary<string, object>))
                .Where(trigger => trigger != null)
                .ToList();
            if (autoTriggers.Count == 0)
            {
                UshellTaskAutoTrigger legacyTrigger = ReadAutoTrigger(ReadDictionary(data, "autoTrigger"));
                if (legacyTrigger != null)
                {
                    autoTriggers.Add(legacyTrigger);
                }
            }

            return new UshellTaskRecord
            {
                TaskId = ReadString(data, "taskId"),
                Description = ReadString(data, "description"),
                LogKeyword = ReadString(data, "logKeyword"),
                CompletionKeyword = ReadString(data, "completionKeyword"),
                Status = ParseStatus(ReadString(data, "status")),
                Reason = ParseReason(ReadString(data, "reason")),
                CreatedAtUtc = ReadString(data, "createdAtUtc"),
                EndedAtUtc = ReadString(data, "endedAtUtc"),
                LastSeenSequence = ReadLong(data, "lastSeenSequence"),
                Buttons = ReadObjectList(data, "buttons")
                    .Select(value => ReadButton(value as Dictionary<string, object>))
                    .Where(button => button != null)
                    .ToList(),
                AutoTriggers = autoTriggers,
                Steps = ReadObjectList(data, "steps")
                    .Select(value => ReadStep(value as Dictionary<string, object>))
                    .Where(step => step != null)
                    .ToList(),
                ReachedStep = ReadStep(ReadDictionary(data, "reachedStep")),
                CapturedLogs = ReadDictionaryList(data, "capturedLogs"),
                ButtonInvocations = ReadObjectList(data, "buttonInvocations")
                    .Select(value => ReadButtonInvocation(value as Dictionary<string, object>))
                    .Where(invocation => invocation != null)
                    .ToList(),
                AutoTriggerInvocations = ReadObjectList(data, "autoTriggerInvocations")
                    .Select(value => ReadAutoTriggerInvocation(value as Dictionary<string, object>))
                    .Where(invocation => invocation != null)
                    .ToList()
            };
        }

        private static UshellTaskButton ReadButton(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            return new UshellTaskButton
            {
                Id = ReadString(data, "buttonId"),
                Label = ReadString(data, "label"),
                Description = ReadString(data, "description"),
                Expression = ReadString(data, "expression")
            };
        }

        private static UshellTaskAutoTrigger ReadAutoTrigger(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            return new UshellTaskAutoTrigger
            {
                Keyword = ReadString(data, "keyword"),
                Description = ReadString(data, "description"),
                Expression = ReadString(data, "expression"),
                Confirm = ReadBool(data, "confirm"),
                HasFired = ReadBool(data, "hasFired")
            };
        }

        private static UshellTaskStep ReadStep(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            return new UshellTaskStep
            {
                Id = ReadString(data, "stepId"),
                Keyword = ReadString(data, "keyword"),
                Description = ReadString(data, "description"),
                HasReached = ReadBool(data, "hasReached")
            };
        }

        private static UshellTaskButtonInvocation ReadButtonInvocation(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            return new UshellTaskButtonInvocation
            {
                ButtonId = ReadString(data, "buttonId"),
                Label = ReadString(data, "label"),
                Expression = ReadString(data, "expression"),
                Success = ReadBool(data, "success"),
                ReturnValue = ReadValue(data, "returnValue"),
                ErrorMessage = ReadString(data, "errorMessage"),
                DurationMs = ReadLong(data, "durationMs"),
                TimestampUtc = ReadString(data, "timestampUtc"),
                CapturedLogs = ReadDictionaryList(data, "capturedLogs")
            };
        }

        private static UshellTaskAutoTriggerInvocation ReadAutoTriggerInvocation(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            return new UshellTaskAutoTriggerInvocation
            {
                Keyword = ReadString(data, "keyword"),
                Description = ReadString(data, "description"),
                Expression = ReadString(data, "expression"),
                Confirm = ReadBool(data, "confirm"),
                Success = ReadBool(data, "success"),
                ReturnValue = ReadValue(data, "returnValue"),
                ErrorMessage = ReadString(data, "errorMessage"),
                DurationMs = ReadLong(data, "durationMs"),
                TimestampUtc = ReadString(data, "timestampUtc"),
                CapturedLogs = ReadDictionaryList(data, "capturedLogs")
            };
        }

        private static UshellTaskStatus ParseStatus(string value)
        {
            switch (value)
            {
                case "step_reached":
                    return UshellTaskStatus.StepReached;
                case "completed":
                    return UshellTaskStatus.Completed;
                case "cancelled":
                    return UshellTaskStatus.Cancelled;
                case "timed_out":
                    return UshellTaskStatus.TimedOut;
                default:
                    return UshellTaskStatus.Active;
            }
        }

        private static UshellTaskCompletionReason ParseReason(string value)
        {
            switch (value)
            {
                case "keyword":
                    return UshellTaskCompletionReason.Keyword;
                case "manual":
                    return UshellTaskCompletionReason.Manual;
                case "step":
                    return UshellTaskCompletionReason.Step;
                case "cancel":
                    return UshellTaskCompletionReason.Cancel;
                case "timeout":
                    return UshellTaskCompletionReason.Timeout;
                case "domain_reload":
                    return UshellTaskCompletionReason.DomainReload;
                default:
                    return UshellTaskCompletionReason.None;
            }
        }

        private static List<Dictionary<string, object>> ReadDictionaryList(Dictionary<string, object> source, string key)
        {
            return ReadObjectList(source, key)
                .Select(value => value as Dictionary<string, object>)
                .Where(value => value != null)
                .Select(value => new Dictionary<string, object>(value))
                .ToList();
        }

        private static List<object> ReadObjectList(Dictionary<string, object> source, string key)
        {
            object value = ReadValue(source, key);
            if (value is List<object> list)
            {
                return list;
            }

            if (value is object[] array)
            {
                return array.ToList();
            }

            return new List<object>();
        }

        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
        {
            return ReadValue(source, key) as Dictionary<string, object>;
        }

        private static object ReadValue(Dictionary<string, object> source, string key)
        {
            return source != null && source.TryGetValue(key, out object value) ? value : null;
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            return ReadValue(source, key)?.ToString();
        }

        private static bool ReadBool(Dictionary<string, object> source, string key)
        {
            object value = ReadValue(source, key);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            return value != null && bool.TryParse(value.ToString(), out bool parsed) && parsed;
        }

        private static long ReadLong(Dictionary<string, object> source, string key)
        {
            object value = ReadValue(source, key);
            if (value is long longValue)
            {
                return longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            return value != null && long.TryParse(value.ToString(), out long parsed) ? parsed : 0;
        }
    }
}
