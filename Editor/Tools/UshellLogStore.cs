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

        static UshellLogStore()
        {
            Application.logMessageReceivedThreaded += OnLogReceived;
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
    }
}
