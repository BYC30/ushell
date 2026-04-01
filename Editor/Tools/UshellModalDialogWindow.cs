using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    internal enum UshellModalDialogResult
    {
        Confirmed,
        Cancelled,
        TimedOut
    }

    internal sealed class UshellModalDialogWindow : EditorWindow
    {
        private TaskCompletionSource<UshellModalDialogResult> _completionSource;
        private string _message;
        private string _confirmLabel;
        private string _cancelLabel;
        private string _timeoutMessage;
        private double _deadline;
        private bool _completed;

        public static Task<UshellModalDialogResult> ShowConfirmationAsync(string title, string message, string confirmLabel, string cancelLabel, double timeoutSeconds)
        {
            UshellModalDialogWindow window = CreateInstance<UshellModalDialogWindow>();
            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(420f, 150f);
            window.maxSize = new Vector2(420f, 220f);
            window._message = message;
            window._confirmLabel = confirmLabel;
            window._cancelLabel = cancelLabel;
            window._timeoutMessage = "超过 10 秒未确认，本次刷新已拒绝。";
            window._deadline = EditorApplication.timeSinceStartup + Math.Max(1d, timeoutSeconds);
            window._completionSource = new TaskCompletionSource<UshellModalDialogResult>();
            window.ShowUtility();
            window.Focus();
            return window._completionSource.Task;
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(_message ?? string.Empty, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(10f);

            double remainingSeconds = Math.Max(0d, _deadline - EditorApplication.timeSinceStartup);
            string timeoutText = string.Format("剩余 {0:0} 秒", Math.Ceiling(remainingSeconds));
            EditorGUILayout.HelpBox(timeoutText, MessageType.Warning);
            if (!string.IsNullOrWhiteSpace(_timeoutMessage))
            {
                EditorGUILayout.LabelField(_timeoutMessage, EditorStyles.wordWrappedMiniLabel);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_cancelLabel ?? "取消", GUILayout.Width(90f)))
            {
                Complete(UshellModalDialogResult.Cancelled);
            }

            if (GUILayout.Button(_confirmLabel ?? "确认", GUILayout.Width(90f)))
            {
                Complete(UshellModalDialogResult.Confirmed);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10f);
        }

        private void OnEditorUpdate()
        {
            if (_completed)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Complete(UshellModalDialogResult.TimedOut);
                return;
            }

            Repaint();
        }

        private void OnDestroy()
        {
            if (_completed)
            {
                return;
            }

            Complete(UshellModalDialogResult.Cancelled, closeWindow: false);
        }

        private void Complete(UshellModalDialogResult result, bool closeWindow = true)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _completionSource?.TrySetResult(result);
            if (closeWindow)
            {
                Close();
            }
        }
    }
}
