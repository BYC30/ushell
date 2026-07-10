using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    [Serializable]
    public sealed class UshellLogRecord
    {
        public long Sequence;
        public string Type;
        public string Message;
        public string StackTrace;
        public string TimestampUtc;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "sequence", Sequence },
                { "type", Type },
                { "message", Message },
                { "stackTrace", StackTrace },
                { "timestampUtc", TimestampUtc }
            };
        }
    }

    [InitializeOnLoad]
    public static class UshellLogStore
    {
        private const int Capacity = 2000;
        private static readonly List<UshellLogRecord> Records = new List<UshellLogRecord>(Capacity);
        private static readonly object SyncRoot = new object();
        private static long _nextSequence;
        private static string ReloadStorageKey => "ushell.logs.reload." + UshellPaths.ProjectKey;

        static UshellLogStore()
        {
            RestoreForDomainReload();
            Application.logMessageReceivedThreaded += OnLogReceived;
            AssemblyReloadEvents.beforeAssemblyReload += PersistForDomainReload;
        }

        public static IReadOnlyList<Dictionary<string, object>> GetEntries(string logType, long? sinceSequence, string keyword, Regex regex, int limit)
        {
            lock (SyncRoot)
            {
                IEnumerable<UshellLogRecord> query = Records;
                if (!string.IsNullOrWhiteSpace(logType))
                {
                    query = query.Where(record => string.Equals(record.Type, logType, StringComparison.OrdinalIgnoreCase));
                }

                if (sinceSequence.HasValue)
                {
                    query = query.Where(record => record.Sequence > sinceSequence.Value);
                }

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    query = query.Where(record => ContainsKeyword(record, keyword));
                }

                if (regex != null)
                {
                    query = query.Where(record => MatchesRegex(record, regex));
                }

                return query
                    .OrderByDescending(record => record.Sequence)
                    .Take(Mathf.Clamp(limit, 1, 1000))
                    .OrderBy(record => record.Sequence)
                    .Select(record => record.ToDictionary())
                    .ToList();
            }
        }

        public static void Clear()
        {
            lock (SyncRoot)
            {
                Records.Clear();
            }
        }

        private static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            UshellLogRecord record = new UshellLogRecord
            {
                Sequence = Interlocked.Increment(ref _nextSequence),
                Message = condition,
                StackTrace = stackTrace,
                Type = type.ToString(),
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            lock (SyncRoot)
            {
                if (Records.Count == Capacity)
                {
                    Records.RemoveAt(0);
                }

                Records.Add(record);
            }
        }

        private static bool ContainsKeyword(UshellLogRecord record, string keyword)
        {
            return Contains(record.Message, keyword) || Contains(record.StackTrace, keyword);
        }

        private static bool MatchesRegex(UshellLogRecord record, Regex regex)
        {
            return Matches(record.Message, regex) || Matches(record.StackTrace, regex);
        }

        private static bool Contains(string source, string keyword)
        {
            return !string.IsNullOrEmpty(source)
                && source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Matches(string source, Regex regex)
        {
            return !string.IsNullOrEmpty(source) && regex.IsMatch(source);
        }

        private static void PersistForDomainReload()
        {
            lock (SyncRoot)
            {
                SessionState.SetString(ReloadStorageKey, MiniJson.Serialize(new Dictionary<string, object>
                {
                    { "nextSequence", _nextSequence },
                    { "records", Records.Select(record => record.ToDictionary()).ToList() }
                }));
            }
        }

        private static void RestoreForDomainReload()
        {
            string json = SessionState.GetString(ReloadStorageKey, null);
            SessionState.EraseString(ReloadStorageKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                Dictionary<string, object> root = MiniJson.Deserialize(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return;
                }

                _nextSequence = ReadLong(root, "nextSequence");
                if (!root.TryGetValue("records", out object recordsValue) || !(recordsValue is List<object> records))
                {
                    return;
                }

                foreach (Dictionary<string, object> data in records.OfType<Dictionary<string, object>>())
                {
                    UshellLogRecord record = new UshellLogRecord
                    {
                        Sequence = ReadLong(data, "sequence"),
                        Type = ReadString(data, "type"),
                        Message = ReadString(data, "message"),
                        StackTrace = ReadString(data, "stackTrace"),
                        TimestampUtc = ReadString(data, "timestampUtc")
                    };
                    Records.Add(record);
                    _nextSequence = Math.Max(_nextSequence, record.Sequence);
                }

                if (Records.Count > Capacity)
                {
                    Records.RemoveRange(0, Records.Count - Capacity);
                }
            }
            catch
            {
                Records.Clear();
                _nextSequence = 0;
            }
        }

        private static long ReadLong(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
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

            return long.TryParse(value.ToString(), out long parsed) ? parsed : 0;
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            return source != null && source.TryGetValue(key, out object value) ? value?.ToString() : null;
        }
    }
}
