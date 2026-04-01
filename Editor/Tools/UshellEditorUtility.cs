using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Ushell.Runtime;

namespace Ushell.Editor
{
    public sealed class UshellRefreshPreparationResult
    {
        public bool Allowed;
        public string Error;
        public string State;
    }

    public static class UshellEditorUtility
    {
        private const double RefreshPlayModeTimeoutSeconds = 10d;

        public static void ClearUnityConsole()
        {
            Type logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
            MethodInfo clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
            clearMethod?.Invoke(null, null);
        }

        public static async Task<UshellRefreshPreparationResult> PrepareRefreshWhilePlayingAsync()
        {
            UshellRefreshPreparationResult result = new UshellRefreshPreparationResult
            {
                Allowed = true,
                State = "not_required"
            };

            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return result;
            }

            UshellModalDialogResult decision = await UshellModalDialogWindow.ShowConfirmationAsync(
                "Refresh Assets",
                "Unity 当前正在 Play。继续刷新会先停止 Play 模式，然后再刷新资源。是否允许继续？",
                "允许刷新",
                "拒绝刷新",
                RefreshPlayModeTimeoutSeconds);

            result.State = ToDecisionState(decision);
            if (decision == UshellModalDialogResult.Cancelled)
            {
                result.Allowed = false;
                result.Error = "User rejected refresh while Unity was in Play mode.";
                return result;
            }

            if (decision == UshellModalDialogResult.TimedOut)
            {
                result.Allowed = false;
                result.Error = "Refresh was rejected because the Play mode confirmation timed out after 10 seconds.";
                return result;
            }

            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                result.State = "confirmed";
                return result;
            }

            EditorApplication.isPlaying = false;
            if (await WaitForConditionAsync(() => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode, RefreshPlayModeTimeoutSeconds))
            {
                result.State = "confirmed";
                return result;
            }

            result.Allowed = false;
            result.State = "playmode_exit_timeout";
            result.Error = "Refresh was rejected because Unity did not exit Play mode within 10 seconds.";
            return result;
        }

        public static bool TryCaptureGameView(string outputPath, out Dictionary<string, object> payload, out string error)
        {
            payload = null;
            error = null;

            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                error = "GameView type is unavailable in this Unity version.";
                return false;
            }

            EditorWindow gameView = EditorWindow.GetWindow(gameViewType);
            if (gameView == null)
            {
                error = "GameView window could not be opened.";
                return false;
            }

            gameView.Focus();
            gameView.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();

            Texture2D texture = null;
            try
            {
                texture = TryCaptureWithScreenCapture();
                if (texture == null)
                {
                    texture = TryCaptureFromScreenPixels(gameView);
                }

                if (texture == null)
                {
                    error = "GameView could not provide a valid screenshot texture.";
                    return false;
                }

                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                payload = new Dictionary<string, object>
                {
                    { "path", outputPath },
                    { "width", texture.width },
                    { "height", texture.height }
                };
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public static bool TryCaptureRuntimeView(string outputPath, out Dictionary<string, object> payload, out string error)
        {
            payload = null;
            error = null;

            Dictionary<string, object> runtimePayload = new Dictionary<string, object>
            {
                { "outputPath", outputPath }
            };

            if (!UshellRuntimeBridge.TryInvoke("capture_screenshot", runtimePayload, out object result, out string runtimeError))
            {
                error = runtimeError;
                return false;
            }

            payload = result as Dictionary<string, object>;
            if (payload == null)
            {
                error = "Runtime screenshot action did not return a valid payload.";
                return false;
            }

            return true;
        }

        private static Texture2D TryCaptureWithScreenCapture()
        {
            Type screenCaptureType = typeof(ScreenCapture);
            MethodInfo captureMethod = screenCaptureType.GetMethod("CaptureScreenshotAsTexture", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (captureMethod == null)
            {
                return null;
            }

            return captureMethod.Invoke(null, null) as Texture2D;
        }

        private static Texture2D TryCaptureFromScreenPixels(EditorWindow gameView)
        {
            Rect position = gameView.position;
            int width = Mathf.RoundToInt(position.width);
            int height = Mathf.RoundToInt(position.height);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            Color[] pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(position.position, width, height);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Task<bool> WaitForConditionAsync(Func<bool> predicate, double timeoutSeconds)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            if (predicate())
            {
                return Task.FromResult(true);
            }

            TaskCompletionSource<bool> completionSource = new TaskCompletionSource<bool>();
            double deadline = EditorApplication.timeSinceStartup + Math.Max(1d, timeoutSeconds);

            void OnUpdate()
            {
                if (predicate())
                {
                    EditorApplication.update -= OnUpdate;
                    completionSource.TrySetResult(true);
                    return;
                }

                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    EditorApplication.update -= OnUpdate;
                    completionSource.TrySetResult(false);
                }
            }

            EditorApplication.update += OnUpdate;
            return completionSource.Task;
        }

        private static string ToDecisionState(UshellModalDialogResult decision)
        {
            switch (decision)
            {
                case UshellModalDialogResult.Confirmed:
                    return "confirmed";
                case UshellModalDialogResult.TimedOut:
                    return "timed_out";
                default:
                    return "cancelled";
            }
        }
    }
}
