using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public static class UshellEditorBridgeServer
    {
        private const int MaxPipeInstances = 16;
        private static readonly object SyncRoot = new object();
        private static readonly List<NamedPipeServerStream> ActivePipes = new List<NamedPipeServerStream>();
        private static CancellationTokenSource _cancellationTokenSource;
        private static Thread _listenThread;
        private static string _state = "stopped";
        private static string _lastError;
        private static string _lastConnectedUtc;
        private static string _lastDisconnectedUtc;
        private static string _pipeName;
        private static int _activeConnections;

        public static string PipeName => string.IsNullOrWhiteSpace(_pipeName) ? UshellPaths.BridgePipeName : _pipeName;

        public static void Start()
        {
            lock (SyncRoot)
            {
                if (_cancellationTokenSource != null)
                {
                    _state = "listening";
                    return;
                }

                _pipeName = UshellPaths.BridgePipeName;
                _cancellationTokenSource = new CancellationTokenSource();
                string pipeName = _pipeName;
                _listenThread = new Thread(() => ListenLoop(_cancellationTokenSource.Token))
                {
                    IsBackground = true,
                    Name = "UshellEditorBridgeServer"
                };
                _listenThread.Start();
                _state = "listening";
                _lastError = null;
                Debug.Log($"[ushell] Unity Bridge listening on pipe '{pipeName}'.");
            }
        }

        public static void Stop()
        {
            CancellationTokenSource cancellationTokenSource;
            List<NamedPipeServerStream> pipes;
            lock (SyncRoot)
            {
                cancellationTokenSource = _cancellationTokenSource;
                _cancellationTokenSource = null;
                _listenThread = null;
                _state = "stopped";
                pipes = ActivePipes.ToList();
                ActivePipes.Clear();
            }

            try
            {
                cancellationTokenSource?.Cancel();
            }
            catch
            {
                // Best effort shutdown.
            }

            foreach (NamedPipeServerStream pipe in pipes)
            {
                try
                {
                    pipe.Dispose();
                }
                catch
                {
                    // Best effort shutdown.
                }
            }

            cancellationTokenSource?.Dispose();
        }

        public static Dictionary<string, object> GetStatusSnapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, object>
                {
                    { "state", _state },
                    { "pipeName", PipeName },
                    { "isConnected", _activeConnections > 0 },
                    { "activeConnections", _activeConnections },
                    { "lastConnectedUtc", _lastConnectedUtc },
                    { "lastDisconnectedUtc", _lastDisconnectedUtc },
                    { "lastError", _lastError }
                };
            }
        }

        private static void ListenLoop(CancellationToken cancellationToken)
        {
            string pipeName = _pipeName;
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        MaxPipeInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    lock (SyncRoot)
                    {
                        ActivePipes.Add(pipe);
                    }

                    pipe.WaitForConnection();
                    NamedPipeServerStream connectedPipe = pipe;
                    pipe = null;

                    RegisterConnected();
                    _ = Task.Run(() => ProcessConnection(connectedPipe, cancellationToken), cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (IOException exception)
                {
                    RecordTransientError(exception.Message);
                    SleepAfterListenError(cancellationToken);
                }
                catch (Exception exception)
                {
                    RecordTransientError(exception.Message);
                    SleepAfterListenError(cancellationToken);
                }
                finally
                {
                    if (pipe != null)
                    {
                        RemovePipe(pipe);
                        pipe.Dispose();
                    }
                }
            }
        }

        private static async Task ProcessConnection(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            try
            {
                using (pipe)
                using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" })
                {
                    string line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return;
                    }

                    object response = await ProcessRequestAsync(line);
                    await writer.WriteLineAsync(MiniJson.Serialize(response));
                    RecordSuccessfulResponse();
                }
            }
            catch (Exception exception)
            {
                RecordTransientError(exception.ToString());
            }
            finally
            {
                RemovePipe(pipe);
                RegisterDisconnected();
            }
        }

        private static async Task<Dictionary<string, object>> ProcessRequestAsync(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return Error("INVALID_REQUEST", "Bridge request was empty.");
            }

            Dictionary<string, object> request = MiniJson.Deserialize(line) as Dictionary<string, object>;
            if (request == null)
            {
                return Error("INVALID_REQUEST", "Bridge request was not a JSON object.");
            }

            string method = request.TryGetValue("method", out object methodValue) ? methodValue?.ToString() : null;
            Dictionary<string, object> parameters = request.TryGetValue("params", out object paramsValue)
                ? paramsValue as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            try
            {
                switch (method)
                {
                    case "tools/list":
                        return Success(await UshellEditorDispatcher.InvokeAsync(() => new Dictionary<string, object>
                        {
                            { "tools", UshellToolRegistry.GetAll().Select(tool => tool.ToMcpDictionary()).ToArray() }
                        }));
                    case "tools/call":
                        return Success(await InvokeToolAsync(parameters));
                    case "bridge/status":
                        return Success(await UshellEditorDispatcher.InvokeAsync(CreateBridgeStatus));
                    case "refresh/status":
                        return Success(await UshellEditorDispatcher.InvokeAsync(() =>
                        {
                            string requestId = parameters.TryGetValue("requestId", out object requestIdValue) ? requestIdValue?.ToString() : null;
                            return UshellRefreshTracker.GetStatus(requestId);
                        }));
                    default:
                        return Error("UNKNOWN_BRIDGE_METHOD", $"Unsupported bridge method '{method}'.");
                }
            }
            catch (Exception exception)
            {
                return Error("UNITY_ERROR", exception.Message, exception.ToString());
            }
        }

        private static async Task<Dictionary<string, object>> InvokeToolAsync(Dictionary<string, object> parameters)
        {
            if (!parameters.TryGetValue("name", out object nameValue))
            {
                return UshellToolEnvelope.FromError("INVALID_ARGUMENT", "tools/call requires a tool name.").ToDictionary();
            }

            string toolName = nameValue?.ToString();
            Dictionary<string, object> arguments = parameters.TryGetValue("arguments", out object argumentsValue)
                ? argumentsValue as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();

            if (!UshellToolRegistry.TryGet(toolName, out UshellToolDefinition tool))
            {
                return UshellToolEnvelope.FromError("UNKNOWN_TOOL", $"Unknown tool '{toolName}'.").ToDictionary();
            }

            UshellToolEnvelope toolResult = tool.AsyncHandler != null
                ? await UshellEditorDispatcher.InvokeAsync(() => tool.AsyncHandler(arguments))
                : await UshellEditorDispatcher.InvokeAsync(() => Task.FromResult(tool.Handler(arguments)));
            return toolResult.ToDictionary();
        }

        private static Dictionary<string, object> CreateBridgeStatus()
        {
            return new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "projectPath", UshellPaths.ProjectPath },
                { "isPlaying", EditorApplication.isPlaying },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "bridgeState", GetStatusSnapshot() },
                { "refreshState", UshellRefreshTracker.GetLatestStatus() }
            };
        }

        private static Dictionary<string, object> Success(object result)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "result", result },
                { "error", null }
            };
        }

        private static Dictionary<string, object> Error(string code, string message, object details = null)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "result", null },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message },
                        { "details", details }
                    }
                }
            };
        }

        private static void RegisterConnected()
        {
            lock (SyncRoot)
            {
                if (_state != "stopped")
                {
                    _state = "listening";
                }

                _activeConnections++;
                _lastConnectedUtc = DateTime.UtcNow.ToString("O");
            }
        }

        private static void RegisterDisconnected()
        {
            lock (SyncRoot)
            {
                _activeConnections = Math.Max(0, _activeConnections - 1);
                _lastDisconnectedUtc = DateTime.UtcNow.ToString("O");
            }
        }

        private static void RecordTransientError(string error)
        {
            lock (SyncRoot)
            {
                _lastError = error;
                if (_state != "stopped")
                {
                    _state = "listening";
                }
            }

            Debug.LogWarning($"[ushell] Unity Bridge transient pipe error: {error}");
        }

        private static void RecordSuccessfulResponse()
        {
            lock (SyncRoot)
            {
                _lastError = null;
                if (_state != "stopped")
                {
                    _state = "listening";
                }
            }
        }

        private static void SleepAfterListenError(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.WaitHandle.WaitOne(500);
            }
            catch
            {
                // Best effort throttling.
            }
        }

        private static void RemovePipe(NamedPipeServerStream pipe)
        {
            lock (SyncRoot)
            {
                ActivePipes.Remove(pipe);
            }
        }
    }
}
