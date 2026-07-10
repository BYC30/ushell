using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public sealed class UshellTaskWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _showActive = true;
        private bool _showHistory;
        private readonly HashSet<string> _expandedLogs = new HashSet<string>();

        [MenuItem("Window/Ushell/AI Tasks")]
        public static void ShowWindow()
        {
            GetWindow<UshellTaskWindow>("AI Tasks");
        }

        private void OnEnable()
        {
            UshellTaskStore.OnChanged += Repaint;
        }

        private void OnDisable()
        {
            UshellTaskStore.OnChanged -= Repaint;
        }

        private void OnGUI()
        {
            List<UshellTaskRecord> tasks = UshellTaskStore.Snapshot(true, 100);
            List<UshellTaskRecord> active = tasks.Where(task => task.Status == UshellTaskStatus.Active).ToList();
            List<UshellTaskRecord> history = tasks.Where(task => task.Status != UshellTaskStatus.Active).ToList();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _showActive = EditorGUILayout.Foldout(_showActive, $"Active ({active.Count})", true);
            if (_showActive)
            {
                foreach (UshellTaskRecord task in active)
                {
                    DrawActiveCard(task);
                }

                if (active.Count == 0)
                {
                    EditorGUILayout.HelpBox("No active AI tasks.", MessageType.Info);
                }
            }

            GUILayout.Space(8);
            _showHistory = EditorGUILayout.Foldout(_showHistory, $"History ({history.Count})", true);
            if (_showHistory)
            {
                foreach (UshellTaskRecord task in history)
                {
                    DrawHistoryCard(task);
                }

                if (history.Count == 0)
                {
                    EditorGUILayout.HelpBox("No completed AI tasks yet.", MessageType.None);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawActiveCard(UshellTaskRecord task)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(task.Description, WrappedBoldLabel());
            GUILayout.Label($"log: {task.LogKeyword}    done: {task.CompletionKeyword}", EditorStyles.miniLabel);
            foreach (UshellTaskAutoTrigger trigger in task.AutoTriggers)
            {
                string state = trigger.HasFired ? "fired" : "armed";
                string confirm = trigger.Confirm ? "confirmed" : "unconfirmed";
                GUILayout.Label($"auto: {trigger.Keyword} ({state}, {confirm})", EditorStyles.miniLabel);
            }

            foreach (UshellTaskStep step in task.Steps.Where(item => !item.HasReached))
            {
                GUILayout.Label($"step: {step.Id} waits for {step.Keyword}", EditorStyles.miniLabel);
            }

            if (task.Buttons.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                foreach (UshellTaskButton button in task.Buttons)
                {
                    string tooltip = string.IsNullOrWhiteSpace(button.Description)
                        ? button.Expression
                        : $"{button.Description}\n\n{button.Expression}";
                    if (GUILayout.Button(new GUIContent(button.Label, tooltip), GUILayout.MaxWidth(180)))
                    {
                        UshellToolEnvelope result = UshellTaskStore.InvokeButton(task.TaskId, button.Id);
                        if (!result.Success)
                        {
                            EditorUtility.DisplayDialog("Task Button Failed", result.Error == null ? "Invocation failed." : result.Error.Message, "OK");
                        }
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            bool expanded = _expandedLogs.Contains(task.TaskId);
            expanded = EditorGUILayout.Foldout(expanded, $"Captured logs: {task.CapturedLogs.Count}", true);
            SetExpanded(task.TaskId, expanded);
            if (expanded)
            {
                DrawRecentLogs(task.CapturedLogs);
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Complete", GUILayout.Width(100)))
            {
                UshellTaskStore.MarkCompletedManual(task.TaskId);
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(100)))
            {
                UshellTaskStore.Cancel(task.TaskId);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            GUILayout.Space(4);
        }

        private void DrawHistoryCard(UshellTaskRecord task)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            DrawStatusBadge(task);
            GUILayout.Label(task.Description, WrappedLabel());
            EditorGUILayout.EndHorizontal();
            GUILayout.Label($"reason: {UshellTaskRecord.FormatReason(task.Reason)}    logs: {task.CapturedLogs.Count}    auto: {task.AutoTriggerInvocations.Count}    ended: {task.EndedAtUtc}", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            GUILayout.Space(2);
        }

        private void DrawStatusBadge(UshellTaskRecord task)
        {
            Color previous = GUI.backgroundColor;
            switch (task.Status)
            {
                case UshellTaskStatus.StepReached:
                    GUI.backgroundColor = new Color(0.35f, 0.65f, 0.9f);
                    break;
                case UshellTaskStatus.Completed:
                    GUI.backgroundColor = new Color(0.35f, 0.75f, 0.45f);
                    break;
                case UshellTaskStatus.TimedOut:
                    GUI.backgroundColor = new Color(0.95f, 0.75f, 0.25f);
                    break;
                default:
                    GUI.backgroundColor = new Color(0.65f, 0.65f, 0.65f);
                    break;
            }

            GUILayout.Label(UshellTaskRecord.FormatStatus(task.Status), EditorStyles.miniButton, GUILayout.Width(86));
            GUI.backgroundColor = previous;
        }

        private void DrawRecentLogs(List<Dictionary<string, object>> logs)
        {
            int start = Mathf.Max(0, logs.Count - 5);
            for (int index = start; index < logs.Count; index++)
            {
                Dictionary<string, object> log = logs[index];
                string message = ReadString(log, "message");
                string type = ReadString(log, "type");
                string sequence = ReadString(log, "sequence");
                EditorGUILayout.LabelField($"#{sequence} [{type}] {message}", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void SetExpanded(string taskId, bool expanded)
        {
            if (expanded)
            {
                _expandedLogs.Add(taskId);
            }
            else
            {
                _expandedLogs.Remove(taskId);
            }
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value))
            {
                return string.Empty;
            }

            return value?.ToString() ?? string.Empty;
        }

        private static GUIStyle WrappedBoldLabel()
        {
            GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
            style.wordWrap = true;
            return style;
        }

        private static GUIStyle WrappedLabel()
        {
            GUIStyle style = new GUIStyle(EditorStyles.label);
            style.wordWrap = true;
            return style;
        }
    }
}
