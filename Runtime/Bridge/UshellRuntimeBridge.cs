using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ushell.Runtime
{
    public delegate object UshellRuntimeAction(object payload);

    public static class UshellRuntimeBridge
    {
        private static readonly Dictionary<string, UshellRuntimeAction> Actions = new Dictionary<string, UshellRuntimeAction>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<object, object>> DebugActions = new Dictionary<string, Func<object, object>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object SyncRoot = new object();

        static UshellRuntimeBridge()
        {
            RegisterBuiltins();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Initialize()
        {
            RegisterBuiltins();
        }

        public static void RegisterAction(string name, UshellRuntimeAction action)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Action name is required.", nameof(name));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (SyncRoot)
            {
                Actions[name] = action;
            }
        }

        public static void RegisterDebugAction(string name, Func<object, object> action)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Debug action name is required.", nameof(name));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (SyncRoot)
            {
                DebugActions[name] = action;
            }
        }

        public static bool TryInvoke(string name, object payload, out object result, out string error)
        {
            lock (SyncRoot)
            {
                if (!Actions.TryGetValue(name, out UshellRuntimeAction action))
                {
                    result = null;
                    error = $"Runtime action '{name}' is not registered.";
                    return false;
                }

                try
                {
                    result = action(payload);
                    error = null;
                    return true;
                }
                catch (Exception exception)
                {
                    result = null;
                    error = exception.ToString();
                    return false;
                }
            }
        }

        public static IReadOnlyCollection<string> GetRegisteredActionNames()
        {
            lock (SyncRoot)
            {
                return new List<string>(Actions.Keys);
            }
        }

        private static void RegisterBuiltins()
        {
            lock (SyncRoot)
            {
                Actions["ping"] = _ => new Dictionary<string, object>
                {
                    { "isPlaying", Application.isPlaying },
                    { "time", Time.realtimeSinceStartup }
                };

                Actions["get_active_scene"] = _ =>
                {
                    Scene scene = SceneManager.GetActiveScene();
                    return new Dictionary<string, object>
                    {
                        { "name", scene.name },
                        { "path", scene.path },
                        { "isLoaded", scene.isLoaded },
                        { "rootCount", scene.rootCount }
                    };
                };

                Actions["find_game_object"] = payload =>
                {
                    string name = ExtractPayloadString(payload, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new InvalidOperationException("Payload must contain a non-empty 'name'.");
                    }

                    GameObject gameObject = GameObject.Find(name);
                    return new Dictionary<string, object>
                    {
                        { "found", gameObject != null },
                        { "name", gameObject != null ? gameObject.name : name },
                        { "activeInHierarchy", gameObject != null && gameObject.activeInHierarchy }
                    };
                };

                Actions["capture_screenshot"] = payload =>
                {
                    string outputPath = ExtractPayloadString(payload, "outputPath");
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        throw new InvalidOperationException("Payload must contain a non-empty 'outputPath'.");
                    }

                    Camera camera = FindCaptureCamera();
                    if (camera == null)
                    {
                        throw new InvalidOperationException("No enabled camera was found for runtime screenshot capture.");
                    }

                    int width = ExtractPayloadInt(payload, "width") ?? Mathf.Max(camera.pixelWidth, Screen.width, 1);
                    int height = ExtractPayloadInt(payload, "height") ?? Mathf.Max(camera.pixelHeight, Screen.height, 1);

                    RenderTexture previousActive = RenderTexture.active;
                    RenderTexture renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                    RenderTexture oldTarget = camera.targetTexture;
                    Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);

                    try
                    {
                        camera.targetTexture = renderTexture;
                        RenderTexture.active = renderTexture;
                        camera.Render();
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        texture.Apply(false, false);
                        File.WriteAllBytes(outputPath, texture.EncodeToPNG());

                        return new Dictionary<string, object>
                        {
                            { "path", outputPath },
                            { "width", width },
                            { "height", height },
                            { "camera", camera.name },
                            { "source", "runtime_camera_render" }
                        };
                    }
                    finally
                    {
                        camera.targetTexture = oldTarget;
                        RenderTexture.active = previousActive;
                        UnityEngine.Object.Destroy(renderTexture);
                        UnityEngine.Object.Destroy(texture);
                    }
                };

                Actions["invoke_debug_action"] = payload =>
                {
                    string actionName = ExtractPayloadString(payload, "name");
                    object actionPayload = ExtractPayloadValue(payload, "payload");
                    if (string.IsNullOrWhiteSpace(actionName))
                    {
                        throw new InvalidOperationException("Payload must contain a non-empty 'name'.");
                    }

                    if (!DebugActions.TryGetValue(actionName, out Func<object, object> action))
                    {
                        throw new InvalidOperationException($"Debug action '{actionName}' is not registered.");
                    }

                    return action(actionPayload);
                };
            }
        }

        private static string ExtractPayloadString(object payload, string key)
        {
            object value = ExtractPayloadValue(payload, key);
            return value?.ToString();
        }

        private static int? ExtractPayloadInt(object payload, string key)
        {
            object value = ExtractPayloadValue(payload, key);
            if (value == null)
            {
                return null;
            }

            if (value is long longValue)
            {
                return (int)longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is double doubleValue)
            {
                return (int)Math.Round(doubleValue);
            }

            if (int.TryParse(value.ToString(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

        private static object ExtractPayloadValue(object payload, string key)
        {
            if (payload is Dictionary<string, object> dictionary && dictionary.TryGetValue(key, out object value))
            {
                return value;
            }

            return null;
        }

        private static Camera FindCaptureCamera()
        {
            Camera[] cameras = Camera.allCameras
                .Where(camera => camera != null && camera.isActiveAndEnabled)
                .OrderByDescending(camera => camera.depth)
                .ToArray();

            Camera mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.isActiveAndEnabled)
            {
                return mainCamera;
            }

            return cameras.FirstOrDefault();
        }
    }
}
