using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Ushell.Runtime;

namespace Ushell.Editor
{
    public static class UshellToolRegistry
    {
        private static readonly Dictionary<string, UshellToolDefinition> Tools = new Dictionary<string, UshellToolDefinition>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            RegisterBuiltins();
        }

        public static IReadOnlyCollection<UshellToolDefinition> GetAll()
        {
            EnsureInitialized();
            return Tools.Values.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static bool TryGet(string name, out UshellToolDefinition tool)
        {
            EnsureInitialized();
            return Tools.TryGetValue(name, out tool);
        }

        private static void RegisterBuiltins()
        {
            Register(UshellEditorTools.CreateHealthCheckTool());
            Register(UshellEditorTools.CreateGetLogsTool());
            Register(UshellEditorTools.CreateClearLogsTool());
            Register(UshellEditorTools.CreateEnterPlayModeTool());
            Register(UshellEditorTools.CreateExitPlayModeTool());
            Register(UshellEditorTools.CreateExecExprTool());
            Register(UshellEditorTools.CreateCaptureScreenshotTool());
            Register(UshellEditorTools.CreateBuildProjectTool());
            Register(UshellEditorTools.CreateGetBuildStatusTool());
            Register(UshellEditorTools.CreateRefreshAssetsTool());
            Register(UshellEditorTools.CreateRuntimeInvokeTool());
        }

        private static void Register(UshellToolDefinition definition)
        {
            Tools[definition.Name] = definition;
        }
    }

    public static class UshellEditorTools
    {
        public static UshellToolDefinition CreateHealthCheckTool()
        {
            return new UshellToolDefinition
            {
                Name = "health_check",
                Description = "Returns the current Unity Editor, compile, PlayMode, and MCP service state.",
                InputSchema = SchemaForObject(new Dictionary<string, object>()),
                Handler = _ => UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                {
                    { "unityVersion", Application.unityVersion },
                    { "projectPath", UshellPaths.ProjectPath },
                    { "isPlaying", EditorApplication.isPlaying },
                    { "isCompiling", EditorApplication.isCompiling },
                    { "isUpdating", EditorApplication.isUpdating },
                    { "serviceState", UshellMcpServer.GetStatusSnapshot() },
                    { "registeredTools", UshellToolRegistry.GetAll().Select(tool => tool.Name).ToArray() },
                    { "registeredRuntimeActions", UshellRuntimeBridge.GetRegisteredActionNames().ToArray() }
                })
            };
        }

        public static UshellToolDefinition CreateGetLogsTool()
        {
            return new UshellToolDefinition
            {
                Name = "get_logs",
                Description = "Returns captured Unity log records with optional filtering by type, sequence, keyword, and regex.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "logType", OptionalString() },
                    { "sinceSequence", OptionalNumber() },
                    { "keyword", OptionalString() },
                    { "regex", OptionalString() },
                    { "limit", OptionalNumber() }
                }),
                Handler = arguments =>
                {
                    string logType = UshellArgumentReader.GetString(arguments, "logType");
                    long? sinceSequence = UshellArgumentReader.GetLong(arguments, "sinceSequence");
                    string keyword = UshellArgumentReader.GetString(arguments, "keyword");
                    string regexPattern = UshellArgumentReader.GetString(arguments, "regex");
                    int limit = UshellArgumentReader.GetInt(arguments, "limit") ?? 200;
                    Regex regex;
                    if (!TryCreateRegex(regexPattern, out regex, out string regexError))
                    {
                        return UshellToolEnvelope.FromError("INVALID_ARGUMENT", regexError);
                    }

                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "entries", UshellLogStore.GetEntries(logType, sinceSequence, keyword, regex, limit) }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateClearLogsTool()
        {
            return new UshellToolDefinition
            {
                Name = "clear_logs",
                Description = "Clears the ushell captured log buffer and attempts to clear the Unity console.",
                InputSchema = SchemaForObject(new Dictionary<string, object>()),
                Handler = _ =>
                {
                    UshellLogStore.Clear();
                    UshellEditorUtility.ClearUnityConsole();
                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "cleared", true }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateEnterPlayModeTool()
        {
            return new UshellToolDefinition
            {
                Name = "enter_playmode",
                Description = "Enters PlayMode if the Editor is currently idle.",
                InputSchema = SchemaForObject(new Dictionary<string, object>()),
                Handler = _ =>
                {
                    if (EditorApplication.isCompiling)
                    {
                        return UshellToolEnvelope.FromError("PLAYMODE_UNAVAILABLE", "Cannot enter PlayMode while the Editor is compiling.");
                    }

                    if (!EditorApplication.isPlaying)
                    {
                        EditorApplication.isPlaying = true;
                    }

                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "isPlaying", EditorApplication.isPlaying }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateExitPlayModeTool()
        {
            return new UshellToolDefinition
            {
                Name = "exit_playmode",
                Description = "Exits PlayMode if the Editor is currently playing.",
                InputSchema = SchemaForObject(new Dictionary<string, object>()),
                Handler = _ =>
                {
                    if (EditorApplication.isPlaying)
                    {
                        EditorApplication.isPlaying = false;
                    }

                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "isPlaying", EditorApplication.isPlaying }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateExecExprTool()
        {
            return new UshellToolDefinition
            {
                Name = "exec_expr",
                Description = "Executes a C# snippet inside the Unity Editor and returns the echoed input together with its result.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "expression", RequiredString() },
                    { "timeoutMs", OptionalNumber() },
                    { "captureLogs", OptionalBoolean() },
                    { "confirm", OptionalBoolean() }
                }, "expression"),
                Handler = arguments =>
                {
                    UshellSettings settings = UshellSettings.Instance;
                    bool confirm = UshellArgumentReader.GetBool(arguments, "confirm") ?? false;
                    string expression = UshellArgumentReader.GetString(arguments, "expression");
                    if (string.IsNullOrWhiteSpace(expression))
                    {
                        expression = UshellArgumentReader.RequireString(arguments, "code");
                    }

                    int timeoutMs = UshellArgumentReader.GetInt(arguments, "timeoutMs") ?? settings.MaxExecutionSeconds * 1000;
                    bool captureLogs = UshellArgumentReader.GetBool(arguments, "captureLogs") ?? true;
                    Dictionary<string, object> echoedArguments = CreateExecExprEcho(expression, timeoutMs, captureLogs, confirm);

                    if (settings.DangerousOperationRequireConfirm && !confirm)
                    {
                        return UshellToolEnvelope.FromError(
                            "UNAUTHORIZED_OPERATION",
                            "exec_expr requires confirm=true when dangerous-operation confirmation is enabled.",
                            new Dictionary<string, object>
                            {
                                { "arguments", echoedArguments }
                            });
                    }

                    UshellCodeExecutionResult result = UshellRoslynExecutor.Execute(expression, captureLogs, timeoutMs);
                    if (!result.Success)
                    {
                        return UshellToolEnvelope.FromError(
                            result.ErrorCode,
                            result.ErrorMessage,
                            new Dictionary<string, object>
                            {
                                { "arguments", echoedArguments },
                                { "execution", result.Details }
                            });
                    }

                    UshellToolEnvelope envelope = UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "arguments", echoedArguments },
                        { "returnValue", result.ReturnValue },
                        { "durationMs", result.DurationMs }
                    });

                    envelope.Logs.AddRange(result.Logs);
                    envelope.Warnings.AddRange(result.Warnings);
                    return envelope;
                }
            };
        }

        public static UshellToolDefinition CreateCaptureScreenshotTool()
        {
            return new UshellToolDefinition
            {
                Name = "capture_screenshot",
                Description = "Captures the current GameView into a PNG file under an allowed output path.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "outputPath", OptionalString() },
                    { "confirm", OptionalBoolean() }
                }),
                Handler = arguments =>
                {
                    bool confirm = UshellArgumentReader.GetBool(arguments, "confirm") ?? false;
                    string requestedPath = UshellArgumentReader.GetString(arguments, "outputPath");
                    string outputPath = UshellPaths.ResolveOutputPath(requestedPath, "Screenshots", "screenshot.png");
                    if (!UshellPaths.EnsureWriteAllowed(outputPath, confirm, out string error))
                    {
                        return UshellToolEnvelope.FromError("UNAUTHORIZED_OPERATION", error);
                    }

                    Dictionary<string, object> payload;
                    string captureError;
                    bool captured = EditorApplication.isPlaying
                        ? UshellEditorUtility.TryCaptureRuntimeView(outputPath, out payload, out captureError)
                        : UshellEditorUtility.TryCaptureGameView(outputPath, out payload, out captureError);

                    if (!captured)
                    {
                        return UshellToolEnvelope.FromError("SCREENSHOT_FAILED", captureError);
                    }

                    return UshellToolEnvelope.FromSuccess(payload);
                }
            };
        }

        public static UshellToolDefinition CreateBuildProjectTool()
        {
            return new UshellToolDefinition
            {
                Name = "build_project",
                Description = "Builds the current project using enabled EditorBuildSettings scenes.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "buildProfile", OptionalString() },
                    { "target", OptionalString() },
                    { "development", OptionalBoolean() },
                    { "outputPath", OptionalString() },
                    { "confirm", OptionalBoolean() }
                }),
                Handler = arguments =>
                {
                    BuildRequest request = new BuildRequest
                    {
                        BuildProfile = UshellArgumentReader.GetString(arguments, "buildProfile"),
                        Target = UshellArgumentReader.GetString(arguments, "target"),
                        Development = UshellArgumentReader.GetBool(arguments, "development") ?? true,
                        RequestedOutputPath = UshellArgumentReader.GetString(arguments, "outputPath"),
                        Confirm = UshellArgumentReader.GetBool(arguments, "confirm") ?? false
                    };

                    return UshellBuildService.Build(request);
                }
            };
        }

        public static UshellToolDefinition CreateRefreshAssetsTool()
        {
            return new UshellToolDefinition
            {
                Name = "refresh_assets",
                Description = "Refreshes the Unity AssetDatabase so externally modified files are reimported and script compilation can start.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "forceSynchronousImport", OptionalBoolean() }
                }),
                AsyncHandler = async arguments =>
                {
                    bool forceSynchronousImport = UshellArgumentReader.GetBool(arguments, "forceSynchronousImport") ?? false;
                    if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        UshellRefreshPreparationResult preparation = await UshellEditorUtility.PrepareRefreshWhilePlayingAsync();
                        if (!preparation.Allowed)
                        {
                            return UshellToolEnvelope.FromError("REFRESH_REJECTED", preparation.Error, new Dictionary<string, object>
                            {
                                { "forceSynchronousImport", forceSynchronousImport },
                                { "playModeConfirmation", preparation.State }
                            });
                        }
                    }

                    ImportAssetOptions options = forceSynchronousImport
                        ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                        : ImportAssetOptions.ForceUpdate;

                    AssetDatabase.Refresh(options);
                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "refreshed", true },
                        { "forceSynchronousImport", forceSynchronousImport },
                        { "playModeStopped", !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode },
                        { "isCompiling", EditorApplication.isCompiling },
                        { "isUpdating", EditorApplication.isUpdating }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateGetBuildStatusTool()
        {
            return new UshellToolDefinition
            {
                Name = "get_build_status",
                Description = "Returns the most recent build summary captured by ushell.",
                InputSchema = SchemaForObject(new Dictionary<string, object>()),
                Handler = _ => UshellToolEnvelope.FromSuccess(UshellBuildService.GetLastBuildSummary())
            };
        }

        public static UshellToolDefinition CreateRuntimeInvokeTool()
        {
            return new UshellToolDefinition
            {
                Name = "runtime_invoke",
                Description = "Invokes a registered runtime action while the Unity player loop is active.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "name", RequiredString() },
                    { "payload", OptionalObject() }
                }, "name"),
                Handler = arguments =>
                {
                    if (!EditorApplication.isPlaying)
                    {
                        return UshellToolEnvelope.FromError("PLAYMODE_UNAVAILABLE", "runtime_invoke requires PlayMode.");
                    }

                    string actionName = UshellArgumentReader.RequireString(arguments, "name");
                    object payload = UshellArgumentReader.GetValue(arguments, "payload");
                    if (!UshellRuntimeBridge.TryInvoke(actionName, payload, out object result, out string error))
                    {
                        return UshellToolEnvelope.FromError("PLAYMODE_UNAVAILABLE", error);
                    }

                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "name", actionName },
                        { "result", result }
                    });
                }
            };
        }

        private static Dictionary<string, object> SchemaForObject(Dictionary<string, object> properties, params string[] required)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };

            if (required != null && required.Length > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        private static Dictionary<string, object> RequiredString()
        {
            Dictionary<string, object> schema = OptionalString();
            schema["minLength"] = 1;
            return schema;
        }

        private static Dictionary<string, object> OptionalString()
        {
            return new Dictionary<string, object> { { "type", "string" } };
        }

        private static Dictionary<string, object> OptionalNumber()
        {
            return new Dictionary<string, object> { { "type", "number" } };
        }

        private static Dictionary<string, object> OptionalBoolean()
        {
            return new Dictionary<string, object> { { "type", "boolean" } };
        }

        private static Dictionary<string, object> OptionalObject()
        {
            return new Dictionary<string, object> { { "type", "object" } };
        }

        private static Dictionary<string, object> CreateExecExprEcho(string expression, int timeoutMs, bool captureLogs, bool confirm)
        {
            return new Dictionary<string, object>
            {
                { "expression", expression },
                { "timeoutMs", timeoutMs },
                { "captureLogs", captureLogs },
                { "confirm", confirm }
            };
        }

        private static bool TryCreateRegex(string pattern, out Regex regex, out string error)
        {
            regex = null;
            error = null;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return true;
            }
            catch (ArgumentException exception)
            {
                error = $"Invalid regex pattern: {exception.Message}";
                return false;
            }
        }
    }

    public static class UshellArgumentReader
    {
        public static string RequireString(Dictionary<string, object> arguments, string key)
        {
            string value = GetString(arguments, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Argument '{key}' is required.");
            }

            return value;
        }

        public static string GetString(Dictionary<string, object> arguments, string key)
        {
            return GetValue(arguments, key)?.ToString();
        }

        public static int? GetInt(Dictionary<string, object> arguments, string key)
        {
            object value = GetValue(arguments, key);
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

        public static long? GetLong(Dictionary<string, object> arguments, string key)
        {
            object value = GetValue(arguments, key);
            if (value == null)
            {
                return null;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is double doubleValue)
            {
                return (long)Math.Round(doubleValue);
            }

            if (long.TryParse(value.ToString(), out long parsed))
            {
                return parsed;
            }

            return null;
        }

        public static bool? GetBool(Dictionary<string, object> arguments, string key)
        {
            object value = GetValue(arguments, key);
            if (value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out bool parsed))
            {
                return parsed;
            }

            return null;
        }

        public static object GetValue(Dictionary<string, object> arguments, string key)
        {
            if (arguments == null)
            {
                return null;
            }

            arguments.TryGetValue(key, out object value);
            return value;
        }
    }
}
