using System;
using System.Collections.Generic;
using UnityEditor;

namespace Ushell.Editor
{
    [InitializeOnLoad]
    public static class UshellRefreshTracker
    {
        private const string StateScheduled = "scheduled";
        private const string StateRefreshing = "refreshing";
        private const string StateWaitingForIdle = "waiting_for_idle";
        private const string StateCompleted = "completed";
        private const string StateFailed = "failed";

        private static readonly string KeyPrefix = "ushell.refresh." + UshellPaths.BridgePipeName + ".";
        private static readonly string RequestIdKey = KeyPrefix + "requestId";
        private static readonly string StateKey = KeyPrefix + "state";
        private static readonly string OptionsKey = KeyPrefix + "options";
        private static readonly string ErrorKey = KeyPrefix + "error";
        private static readonly string ScheduledUtcKey = KeyPrefix + "scheduledUtc";
        private static readonly string RefreshCalledUtcKey = KeyPrefix + "refreshCalledUtc";
        private static readonly string CompletedUtcKey = KeyPrefix + "completedUtc";

        static UshellRefreshTracker()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            if (string.Equals(EditorPrefs.GetString(StateKey, null), StateScheduled, StringComparison.OrdinalIgnoreCase))
            {
                EditorApplication.delayCall += ExecutePendingRefresh;
            }
        }

        public static bool TryScheduleRefresh(ImportAssetOptions options, out Dictionary<string, object> status)
        {
            if (TryGetActiveStatus(out status))
            {
                return false;
            }

            string requestId = Guid.NewGuid().ToString("N");
            EditorPrefs.SetString(RequestIdKey, requestId);
            EditorPrefs.SetString(StateKey, StateScheduled);
            EditorPrefs.SetInt(OptionsKey, (int)options);
            EditorPrefs.DeleteKey(ErrorKey);
            EditorPrefs.SetString(ScheduledUtcKey, DateTime.UtcNow.ToString("O"));
            EditorPrefs.DeleteKey(RefreshCalledUtcKey);
            EditorPrefs.DeleteKey(CompletedUtcKey);

            EditorApplication.delayCall += ExecutePendingRefresh;
            status = GetStatus(requestId);
            return true;
        }

        public static bool TryGetActiveStatus(out Dictionary<string, object> status)
        {
            string currentState = EditorPrefs.GetString(StateKey, null);
            if (IsActiveState(currentState))
            {
                status = GetLatestStatus();
                return true;
            }

            status = null;
            return false;
        }

        public static Dictionary<string, object> GetLatestStatus()
        {
            return BuildStatus(null, false);
        }

        public static Dictionary<string, object> GetStatus(string requestId)
        {
            return BuildStatus(requestId, true);
        }

        private static void ExecutePendingRefresh()
        {
            if (!string.Equals(EditorPrefs.GetString(StateKey, null), StateScheduled, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EditorPrefs.SetString(StateKey, StateRefreshing);
            EditorPrefs.SetString(RefreshCalledUtcKey, DateTime.UtcNow.ToString("O"));
            try
            {
                ImportAssetOptions options = (ImportAssetOptions)EditorPrefs.GetInt(OptionsKey, (int)ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh(options);
                EditorPrefs.SetString(StateKey, StateWaitingForIdle);
            }
            catch (Exception exception)
            {
                EditorPrefs.SetString(StateKey, StateFailed);
                EditorPrefs.SetString(ErrorKey, exception.Message);
                EditorPrefs.SetString(CompletedUtcKey, DateTime.UtcNow.ToString("O"));
            }
        }

        private static void Tick()
        {
            string state = EditorPrefs.GetString(StateKey, null);
            if (string.Equals(state, StateScheduled, StringComparison.OrdinalIgnoreCase))
            {
                ExecutePendingRefresh();
                return;
            }

            if (!string.Equals(state, StateWaitingForIdle, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, StateRefreshing, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorPrefs.SetString(StateKey, StateCompleted);
            EditorPrefs.SetString(CompletedUtcKey, DateTime.UtcNow.ToString("O"));
        }

        private static bool IsActiveState(string state)
        {
            return string.Equals(state, StateScheduled, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, StateRefreshing, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, StateWaitingForIdle, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildStatus(string requestedId, bool requireMatch)
        {
            string currentId = EditorPrefs.GetString(RequestIdKey, null);
            string state = EditorPrefs.GetString(StateKey, "none");
            bool matches = !string.IsNullOrWhiteSpace(requestedId) && string.Equals(requestedId, currentId, StringComparison.Ordinal);

            if (requireMatch && !matches)
            {
                return new Dictionary<string, object>
                {
                    { "requestId", requestedId },
                    { "state", "not_found" },
                    { "currentRequestId", currentId },
                    { "isCompiling", EditorApplication.isCompiling },
                    { "isUpdating", EditorApplication.isUpdating }
                };
            }

            return new Dictionary<string, object>
            {
                { "requestId", currentId },
                { "state", state },
                { "scheduledUtc", EditorPrefs.GetString(ScheduledUtcKey, null) },
                { "refreshCalledUtc", EditorPrefs.GetString(RefreshCalledUtcKey, null) },
                { "completedUtc", EditorPrefs.GetString(CompletedUtcKey, null) },
                { "error", EditorPrefs.GetString(ErrorKey, null) },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating }
            };
        }
    }
}
