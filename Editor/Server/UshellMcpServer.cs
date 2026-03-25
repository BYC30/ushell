using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public static class UshellMcpServer
    {
        private static readonly object SyncRoot = new object();
        private static HttpListener _listener;
        private static CancellationTokenSource _cancellationTokenSource;
        private static string _status = "stopped";
        private static string _lastError;

        public static void Start()
        {
            lock (SyncRoot)
            {
                if (_listener != null)
                {
                    _status = "running";
                    return;
                }

                if (EditorApplication.isCompiling)
                {
                    _status = "waiting_for_compile";
                    return;
                }

                try
                {
                    UshellToolRegistry.EnsureInitialized();
                    _cancellationTokenSource = new CancellationTokenSource();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{UshellSettings.Instance.Port}/mcp/");
                    _listener.Prefixes.Add($"http://localhost:{UshellSettings.Instance.Port}/mcp/");
                    _listener.Start();
                    Task.Run(() => ListenLoopAsync(_cancellationTokenSource.Token));
                    _status = "running";
                    _lastError = null;
                    Debug.Log($"[ushell] MCP server listening on http://127.0.0.1:{UshellSettings.Instance.Port}/mcp");
                }
                catch (Exception exception)
                {
                    _status = "error";
                    _lastError = exception.Message;
                    Debug.LogError($"[ushell] Failed to start MCP server: {exception}");
                    StopInternal();
                }
            }
        }

        public static void Stop()
        {
            lock (SyncRoot)
            {
                StopInternal();
                _status = "stopped";
            }
        }

        public static void Restart()
        {
            Stop();
            EditorApplication.delayCall += Start;
        }

        public static string GetStatusSummary()
        {
            lock (SyncRoot)
            {
                return _status;
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
                return new Dictionary<string, object>
                {
                    { "state", _status },
                    { "port", UshellSettings.Instance.Port },
                    { "endpoint", $"http://127.0.0.1:{UshellSettings.Instance.Port}/mcp" },
                    { "lastError", _lastError }
                };
            }
        }

        private static async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequestAsync(context), cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    lock (SyncRoot)
                    {
                        _status = "error";
                        _lastError = exception.Message;
                    }

                    Debug.LogError($"[ushell] HTTP listen loop error: {exception}");
                    try
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (!IsOriginAllowed(context.Request))
                {
                    await WriteJsonAsync(context.Response, 403, new Dictionary<string, object> { { "error", "Origin is not allowed." } });
                    return;
                }

                if (context.Request.HttpMethod == "GET")
                {
                    await WriteJsonAsync(context.Response, 405, new Dictionary<string, object> { { "error", "This v1 server supports POST JSON-RPC requests only." } });
                    return;
                }

                if (context.Request.HttpMethod != "POST")
                {
                    await WriteJsonAsync(context.Response, 405, new Dictionary<string, object> { { "error", "Method not allowed." } });
                    return;
                }

                using (StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    string body = await reader.ReadToEndAsync();
                    object parsed = MiniJson.Deserialize(body);
                    if (parsed is IList batch)
                    {
                        List<object> responses = new List<object>();
                        foreach (object item in batch)
                        {
                            object response = await ProcessJsonRpcAsync(item as Dictionary<string, object>);
                            if (response != null)
                            {
                                responses.Add(response);
                            }
                        }

                        if (responses.Count == 0)
                        {
                            context.Response.StatusCode = 202;
                            context.Response.Close();
                            return;
                        }

                        await WriteJsonAsync(context.Response, 200, responses);
                        return;
                    }

                    object singleResponse = await ProcessJsonRpcAsync(parsed as Dictionary<string, object>);
                    if (singleResponse == null)
                    {
                        context.Response.StatusCode = 202;
                        context.Response.Close();
                        return;
                    }

                    await WriteJsonAsync(context.Response, 200, singleResponse);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ushell] Request processing failed: {exception}");
                try
                {
                    await WriteJsonAsync(context.Response, 500, BuildErrorResponse(null, -32603, exception.Message));
                }
                catch
                {
                    context.Response.Close();
                }
            }
        }

        private static async Task<object> ProcessJsonRpcAsync(Dictionary<string, object> request)
        {
            if (request == null)
            {
                return BuildErrorResponse(null, -32700, "Invalid JSON payload.");
            }

            object id = request.TryGetValue("id", out object idValue) ? idValue : null;
            string method = request.TryGetValue("method", out object methodValue) ? methodValue?.ToString() : null;
            Dictionary<string, object> parameters = request.TryGetValue("params", out object paramsValue) ? paramsValue as Dictionary<string, object> : null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return BuildResultResponse(id, new Dictionary<string, object>
                        {
                            { "protocolVersion", "2025-11-05" },
                            { "capabilities", new Dictionary<string, object>
                                {
                                    { "tools", new Dictionary<string, object> { { "listChanged", false } } }
                                }
                            },
                            { "serverInfo", new Dictionary<string, object>
                                {
                                    { "name", "ushell" },
                                    { "title", "ushell" },
                                    { "version", "0.1.0" }
                                }
                            },
                            { "instructions", "Unity embedded MCP server for local editor automation." }
                        });
                    case "notifications/initialized":
                        return null;
                    case "ping":
                        return BuildResultResponse(id, new Dictionary<string, object>());
                    case "tools/list":
                        return BuildResultResponse(id, new Dictionary<string, object>
                        {
                            { "tools", UshellToolRegistry.GetAll().Select(tool => tool.ToMcpDictionary()).ToArray() }
                        });
                    case "tools/call":
                        return await HandleToolCallAsync(id, parameters);
                    default:
                        return BuildErrorResponse(id, -32601, $"Unsupported method '{method}'.");
                }
            }
            catch (Exception exception)
            {
                return BuildErrorResponse(id, -32603, exception.Message);
            }
        }

        private static async Task<Dictionary<string, object>> HandleToolCallAsync(object id, Dictionary<string, object> parameters)
        {
            if (parameters == null || !parameters.TryGetValue("name", out object nameValue))
            {
                return BuildErrorResponse(id, -32602, "tools/call requires a tool name.");
            }

            string toolName = nameValue?.ToString();
            Dictionary<string, object> arguments = parameters.TryGetValue("arguments", out object argumentsValue)
                ? argumentsValue as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
            if (!UshellToolRegistry.TryGet(toolName, out UshellToolDefinition tool))
            {
                return BuildErrorResponse(id, -32601, $"Unknown tool '{toolName}'.");
            }

            UshellToolEnvelope toolResult = await UshellEditorDispatcher.InvokeAsync(() => tool.Handler(arguments));
            return BuildResultResponse(id, new Dictionary<string, object>
            {
                { "content", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", BuildToolSummary(toolName, toolResult) }
                        }
                    }
                },
                { "structuredContent", toolResult.ToDictionary() },
                { "isError", !toolResult.Success }
            });
        }

        private static bool IsOriginAllowed(HttpListenerRequest request)
        {
            string origin = request.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin))
            {
                return true;
            }

            return origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
        {
            string json = MiniJson.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private static Dictionary<string, object> BuildResultResponse(object id, object result)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
        }

        private static Dictionary<string, object> BuildErrorResponse(object id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            };
        }

        private static string BuildToolSummary(string toolName, UshellToolEnvelope result)
        {
            if (!result.Success)
            {
                return $"{toolName} failed: {result.Error?.Code} - {result.Error?.Message}";
            }

            return $"{toolName} completed successfully.";
        }

        private static void StopInternal()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Best effort shutdown.
            }
            finally
            {
                _listener = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }
    }
}
