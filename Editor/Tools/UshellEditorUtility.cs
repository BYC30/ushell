using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Ushell.Runtime;

namespace Ushell.Editor
{
    public static class UshellEditorUtility
    {
        public static void ClearUnityConsole()
        {
            Type logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
            MethodInfo clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
            clearMethod?.Invoke(null, null);
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
    }
}
