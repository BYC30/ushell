using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public static class UshellMcpProcessSupervisor
    {
        private static readonly object SyncRoot = new object();
        private static Process _process;
        private static string _state = "stopped";
        private static string _lastError;
        private static string _lastStartUtc;
        private static int _activePort;
        private static int _preferredPort;
        private static int _externalProcessId;
        private static bool _usingAlternatePort;
        private static readonly string LastActivePortKey = "ushell.mcp." + UshellPaths.ProjectKey + ".lastActivePort";

        public static void EnsureStarted()
        {
            lock (SyncRoot)
            {
                int preferredPort = UshellSettings.Instance.Port;
                if (_process != null && !_process.HasExited && _preferredPort == preferredPort)
                {
                    _state = "running";
                    return;
                }

                int lastActivePort = EditorPrefs.GetInt(LastActivePortKey, 0);
                if (lastActivePort > 0 && lastActivePort != preferredPort)
                {
                    ProjectEndpointInfo lastEndpointInfo = TryGetProjectEndpointInfo(lastActivePort);
                    if (lastEndpointInfo.Responsive && IsCurrentProject(lastEndpointInfo.ProjectPath))
                    {
                        _state = "running_existing";
                        _lastError = null;
                        _activePort = lastActivePort;
                        _preferredPort = preferredPort;
                        _externalProcessId = lastEndpointInfo.ProcessId;
                        _usingAlternatePort = lastActivePort != preferredPort;
                        return;
                    }
                }

                PortSelection selection = SelectPort(preferredPort);
                if (selection.ExistingSameProject)
                {
                    _state = "running_existing";
                    _lastError = null;
                    _activePort = selection.Port;
                    _preferredPort = preferredPort;
                    _externalProcessId = selection.ProcessId;
                    _usingAlternatePort = selection.Port != preferredPort;
                    EditorPrefs.SetInt(LastActivePortKey, selection.Port);
                    return;
                }

                string executablePath = UshellPaths.McpServerExecutablePath;
                if (!File.Exists(executablePath))
                {
                    _state = "missing_executable";
                    _lastError = $"MCP server executable was not found at '{executablePath}'. Run publish.ps1 to build Server/publish/Ushell.McpServer.exe.";
                    UnityEngine.Debug.LogWarning($"[ushell] {_lastError}");
                    return;
                }

                try
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = BuildArguments(selection.Port),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    _process = Process.Start(startInfo);
                    _state = _process == null ? "error" : "running";
                    _lastError = _process == null ? "Process.Start returned null." : null;
                    _activePort = selection.Port;
                    _preferredPort = preferredPort;
                    _externalProcessId = _process != null ? _process.Id : 0;
                    _usingAlternatePort = selection.Port != preferredPort;
                    _lastStartUtc = DateTime.UtcNow.ToString("O");
                    EditorPrefs.SetInt(LastActivePortKey, selection.Port);
                    if (_usingAlternatePort)
                    {
                        _lastError = $"Preferred port {preferredPort} is already used by another project. Started on {selection.Port}.";
                        UnityEngine.Debug.LogWarning($"[ushell] {_lastError}");
                    }

                    UnityEngine.Debug.Log($"[ushell] MCP server process started on http://127.0.0.1:{selection.Port}/mcp.");
                }
                catch (Exception exception)
                {
                    _state = "error";
                    _lastError = exception.Message;
                    UnityEngine.Debug.LogError($"[ushell] Failed to start MCP server process: {exception}");
                }
            }
        }

        public static void Restart()
        {
            Restart(UshellSettings.Instance.Port);
        }

        public static void Restart(int previousPort)
        {
            StopActive(_activePort > 0 ? _activePort : previousPort);
            EditorApplication.delayCall += EnsureStarted;
        }

        public static void Stop()
        {
            StopActive(_activePort > 0 ? _activePort : UshellSettings.Instance.Port);
        }

        public static void StopActive(int port)
        {
            lock (SyncRoot)
            {
                if (port > 0)
                {
                    SendShutdown(port);
                }

                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        if (!_process.WaitForExit(1500))
                        {
                            _process.Kill();
                        }
                    }
                }
                catch
                {
                    // Best effort shutdown.
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                    _state = "stopped";
                    _activePort = 0;
                    _externalProcessId = 0;
                    _usingAlternatePort = false;
                    EditorPrefs.DeleteKey(LastActivePortKey);
                }
            }
        }

        public static string GetStatusSummary()
        {
            lock (SyncRoot)
            {
                return _state;
            }
        }

        public static string GetLastError()
        {
            lock (SyncRoot)
            {
                return _lastError;
            }
        }

        public static Dictionary<string, object> GetStatusSnapshot()
        {
            lock (SyncRoot)
            {
                int preferredPort = UshellSettings.Instance.Port;
                int port = _activePort > 0 ? _activePort : preferredPort;
                bool processAlive = _process != null && !_process.HasExited;
                object processId = processAlive ? (object)_process.Id : (_externalProcessId > 0 ? (object)_externalProcessId : null);
                return new Dictionary<string, object>
                {
                    { "state", _state },
                    { "port", port },
                    { "preferredPort", preferredPort },
                    { "usingAlternatePort", _usingAlternatePort },
                    { "endpoint", $"http://127.0.0.1:{port}/mcp" },
                    { "processId", processId },
                    { "executablePath", UshellPaths.McpServerExecutablePath },
                    { "pipeName", UshellPaths.BridgePipeName },
                    { "lastStartUtc", _lastStartUtc },
                    { "lastError", _lastError }
                };
            }
        }

        private static string BuildArguments(int port)
        {
            return string.Join(" ", new[]
            {
                "--port", port.ToString(),
                "--pipe", Quote(UshellPaths.BridgePipeName),
                "--project", Quote(UshellPaths.ProjectPath)
            });
        }

        private static PortSelection SelectPort(int preferredPort)
        {
            ProjectEndpointInfo endpointInfo = TryGetProjectEndpointInfo(preferredPort);
            if (endpointInfo.Responsive && IsCurrentProject(endpointInfo.ProjectPath))
            {
                return new PortSelection
                {
                    Port = preferredPort,
                    ExistingSameProject = true,
                    ProcessId = endpointInfo.ProcessId
                };
            }

            if (!IsPortInUse(preferredPort))
            {
                return new PortSelection
                {
                    Port = preferredPort
                };
            }

            for (int port = preferredPort + 1; port <= 65535 && port < preferredPort + 200; port++)
            {
                if (!IsPortInUse(port))
                {
                    return new PortSelection
                    {
                        Port = port
                    };
                }
            }

            throw new InvalidOperationException($"Could not find a free MCP port near {preferredPort}.");
        }

        private static bool IsCurrentProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(UshellPaths.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool IsEndpointResponsive(int port)
        {
            return TryGetProjectEndpointInfo(port).Responsive;
        }

        private static ProjectEndpointInfo TryGetProjectEndpointInfo(int port)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/mcp/");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 500;
                byte[] bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ushell/status\"}");
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return new ProjectEndpointInfo { Responsive = true };
                    }

                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd();
                        string projectPath = TryReadProjectPath(body);
                        return new ProjectEndpointInfo
                        {
                            Responsive = true,
                            ProjectPath = projectPath,
                            ProcessId = TryReadProcessId(body)
                        };
                    }
                }
            }
            catch
            {
                return new ProjectEndpointInfo();
            }
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        private static string TryReadProjectPath(string json)
        {
            try
            {
                Dictionary<string, object> response = MiniJson.Deserialize(json) as Dictionary<string, object>;
                Dictionary<string, object> result = GetDictionary(response, "result");
                string directProjectPath = GetString(result, "projectPath");
                if (!string.IsNullOrWhiteSpace(directProjectPath))
                {
                    return directProjectPath;
                }

                Dictionary<string, object> structuredContent = GetDictionary(result, "structuredContent");
                Dictionary<string, object> data = GetDictionary(structuredContent, "data");
                Dictionary<string, object> serviceState = GetDictionary(data, "serviceState");

                string projectPath = GetString(serviceState, "projectPath");
                if (!string.IsNullOrWhiteSpace(projectPath))
                {
                    return projectPath;
                }

                return GetString(data, "projectPath");
            }
            catch
            {
                return null;
            }
        }

        private static int TryReadProcessId(string json)
        {
            try
            {
                Dictionary<string, object> response = MiniJson.Deserialize(json) as Dictionary<string, object>;
                Dictionary<string, object> result = GetDictionary(response, "result");
                object processId = GetValue(result, "processId");
                if (processId is long longValue)
                {
                    return (int)longValue;
                }

                if (processId is int intValue)
                {
                    return intValue;
                }

                if (processId is double doubleValue)
                {
                    return (int)Math.Round(doubleValue);
                }

                return int.TryParse(processId?.ToString(), out int parsed) ? parsed : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary != null && dictionary.TryGetValue(key, out object value))
            {
                return value as Dictionary<string, object>;
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            return GetValue(dictionary, key)?.ToString();
        }

        private static object GetValue(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary != null && dictionary.TryGetValue(key, out object value))
            {
                return value;
            }

            return null;
        }

        private static void SendShutdown(int port)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/mcp/");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 500;
                byte[] bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ushell/shutdown\"}");
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (request.GetResponse())
                {
                }
            }
            catch
            {
                // The process may already be gone.
            }
        }

        private sealed class PortSelection
        {
            public int Port;
            public bool ExistingSameProject;
            public int ProcessId;
        }

        private sealed class ProjectEndpointInfo
        {
            public bool Responsive;
            public string ProjectPath;
            public int ProcessId;
        }
    }
}
