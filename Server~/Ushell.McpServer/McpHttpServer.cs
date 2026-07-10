using System.Net;
using System.Text;

namespace Ushell.McpServer;

internal sealed class McpHttpServer
{
    private const string ProtocolVersion = "2025-11-05";
    private const int HealthBridgeTimeoutMs = 2000;
    private const int RefreshStatusBridgeTimeoutMs = 2000;
    private const int TaskStatusBridgeTimeoutMs = 2000;
    private const int ToolListBridgeTimeoutMs = 2000;
    private readonly BridgeClient _bridgeClient;
    private readonly ServerOptions _options;
    private readonly HttpListener _listener = new();
    private readonly object _toolCacheLock = new();
    private IReadOnlyList<Dictionary<string, object?>> _cachedTools = ToolCatalog.CreateDefaultTools();
    private volatile bool _shutdownRequested;

    public McpHttpServer(ServerOptions options, BridgeClient bridgeClient)
    {
        _options = options;
        _bridgeClient = bridgeClient;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_options.Port}/mcp/");
        _listener.Prefixes.Add($"http://localhost:{_options.Port}/mcp/");
        _listener.Start();
        Console.Error.WriteLine($"[ushell.mcp] listening on http://127.0.0.1:{_options.Port}/mcp");

        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => ProcessRequestAsync(context, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
        }
    }

    public void Stop()
    {
        _shutdownRequested = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Best effort shutdown.
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsOriginAllowed(context.Request))
            {
                await WriteJsonAsync(context.Response, 403, new Dictionary<string, object?> { ["error"] = "Origin is not allowed." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(context.Response, 405, new Dictionary<string, object?> { ["error"] = "This server supports POST JSON-RPC requests only." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                await WriteJsonAsync(context.Response, 405, new Dictionary<string, object?> { ["error"] = "Method not allowed." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            using StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            object? parsed = JsonUtil.Deserialize(body);
            if (parsed is List<object?> batch)
            {
                List<object?> responses = new();
                foreach (object? item in batch)
                {
                    object? response = await ProcessJsonRpcAsync(JsonUtil.AsObject(item), cancellationToken).ConfigureAwait(false);
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

                await WriteJsonAsync(context.Response, 200, responses, cancellationToken).ConfigureAwait(false);
                return;
            }

            object? singleResponse = await ProcessJsonRpcAsync(JsonUtil.AsObject(parsed), cancellationToken).ConfigureAwait(false);
            if (singleResponse == null)
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            await WriteJsonAsync(context.Response, 200, singleResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await WriteJsonAsync(context.Response, 500, BuildErrorResponse(null, -32603, exception.Message), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                context.Response.Close();
            }
        }
    }

    private async Task<object?> ProcessJsonRpcAsync(Dictionary<string, object?> request, CancellationToken cancellationToken)
    {
        if (request.Count == 0)
        {
            return BuildErrorResponse(null, -32700, "Invalid JSON payload.");
        }

        object? id = JsonUtil.Get(request, "id");
        string? method = JsonUtil.Get(request, "method")?.ToString();
        Dictionary<string, object?> parameters = JsonUtil.AsObject(JsonUtil.Get(request, "params"));

        switch (method)
        {
            case "initialize":
                return BuildResultResponse(id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "ushell",
                        ["title"] = "ushell",
                        ["version"] = "0.1.0"
                    },
                    ["instructions"] = "Unity MCP server with an external process and Unity Editor Bridge."
                });
            case "notifications/initialized":
                return null;
            case "ping":
                return BuildResultResponse(id, new Dictionary<string, object?>());
            case "ushell/status":
                return BuildResultResponse(id, CreateServiceState());
            case "tools/list":
                return BuildResultResponse(id, new Dictionary<string, object?> { ["tools"] = await GetToolsAsync(cancellationToken).ConfigureAwait(false) });
            case "tools/call":
                return await HandleToolCallAsync(id, parameters, cancellationToken).ConfigureAwait(false);
            case "ushell/shutdown":
                _shutdownRequested = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    Stop();
                });
                return BuildResultResponse(id, new Dictionary<string, object?> { ["shutdown"] = true });
            default:
                return BuildErrorResponse(id, -32601, $"Unsupported method '{method}'.");
        }
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> GetToolsAsync(CancellationToken cancellationToken)
    {
        BridgeResponse bridgeResponse = await _bridgeClient.SendAsync(
            "tools/list",
            new Dictionary<string, object?>(),
            CreateShortBridgeTimeout(ToolListBridgeTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        if (bridgeResponse.Success)
        {
            Dictionary<string, object?> result = JsonUtil.AsObject(bridgeResponse.Result);
            if (JsonUtil.Get(result, "tools") is List<object?> tools)
            {
                List<Dictionary<string, object?>> normalizedTools = tools.Select(JsonUtil.AsObject).Where(tool => tool.Count > 0).ToList();
                if (normalizedTools.Count > 0)
                {
                    lock (_toolCacheLock)
                    {
                        _cachedTools = normalizedTools;
                    }
                }
            }
        }

        lock (_toolCacheLock)
        {
            return _cachedTools.Select(tool => new Dictionary<string, object?>(tool, StringComparer.Ordinal)).ToArray();
        }
    }

    private async Task<Dictionary<string, object?>> HandleToolCallAsync(object? id, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("name", out object? nameValue))
        {
            return BuildErrorResponse(id, -32602, "tools/call requires a tool name.");
        }

        string toolName = nameValue?.ToString() ?? string.Empty;
        Dictionary<string, object?> arguments = JsonUtil.AsObject(JsonUtil.Get(parameters, "arguments"));
        if (string.Equals(toolName, "health_check", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleHealthCheckAsync(id, arguments, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(toolName, "refresh_assets", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRefreshAssetsAsync(id, arguments, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(toolName, "assign_task", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAssignTaskAsync(id, arguments, cancellationToken).ConfigureAwait(false);
        }

        BridgeResponse bridgeResponse = await CallToolBridgeAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        return BuildResultResponse(id, ToMcpToolResult(toolName, bridgeResponse));
    }

    private async Task<Dictionary<string, object?>> HandleHealthCheckAsync(object? id, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        BridgeResponse bridgeResponse = await CallToolBridgeAsync("health_check", arguments, cancellationToken, CreateShortBridgeTimeout(HealthBridgeTimeoutMs)).ConfigureAwait(false);
        if (bridgeResponse.Success)
        {
            return BuildResultResponse(id, ToMcpToolResult("health_check", bridgeResponse));
        }

        Dictionary<string, object?> envelope = SuccessEnvelope(new Dictionary<string, object?>
        {
            ["serviceState"] = CreateServiceState(),
            ["bridgeState"] = CreateDisconnectedBridgeState(bridgeResponse)
        });

        return BuildResultResponse(id, ToMcpToolResult("health_check", BridgeResponse.FromSuccess(envelope)));
    }

    private async Task<Dictionary<string, object?>> HandleRefreshAssetsAsync(object? id, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        int timeoutMs = ReadInt(arguments, "timeoutMs") ?? _options.RefreshTimeoutMs;
        BridgeResponse bridgeResponse = await CallToolBridgeAsync("refresh_assets", arguments, cancellationToken).ConfigureAwait(false);
        if (!bridgeResponse.Success)
        {
            return BuildResultResponse(id, ToMcpToolResult("refresh_assets", bridgeResponse));
        }

        Dictionary<string, object?> envelope = JsonUtil.AsObject(bridgeResponse.Result);
        Dictionary<string, object?> data = JsonUtil.AsObject(JsonUtil.Get(envelope, "data"));
        string? requestId = JsonUtil.Get(data, "refreshRequestId")?.ToString();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return BuildResultResponse(id, ToMcpToolResult("refresh_assets", bridgeResponse));
        }

        BridgeResponse waitResponse = await WaitForRefreshAsync(requestId, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!waitResponse.Success)
        {
            return BuildResultResponse(id, ToMcpToolResult("refresh_assets", waitResponse));
        }

        data["refreshStatus"] = waitResponse.Result;
        envelope["data"] = data;
        return BuildResultResponse(id, ToMcpToolResult("refresh_assets", BridgeResponse.FromSuccess(envelope)));
    }

    private async Task<Dictionary<string, object?>> HandleAssignTaskAsync(object? id, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        int timeoutMs = Math.Max(1, ReadInt(arguments, "timeoutMs") ?? 1800000);
        BridgeResponse startResponse = await CallToolBridgeAsync("assign_task", arguments, cancellationToken).ConfigureAwait(false);
        if (!startResponse.Success)
        {
            return BuildResultResponse(id, ToMcpToolResult("assign_task", startResponse));
        }

        Dictionary<string, object?> envelope = JsonUtil.AsObject(startResponse.Result);
        if (JsonUtil.Get(envelope, "success") is not bool startSucceeded || !startSucceeded)
        {
            return BuildResultResponse(id, ToMcpToolResult("assign_task", startResponse));
        }

        Dictionary<string, object?> data = JsonUtil.AsObject(JsonUtil.Get(envelope, "data"));
        string? taskId = JsonUtil.Get(data, "taskId")?.ToString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return BuildResultResponse(id, ToMcpToolResult("assign_task", BridgeResponse.FromError(
                "TASK_START_FAILED",
                "Unity accepted assign_task without returning a task id.",
                envelope)));
        }

        BridgeResponse waitResponse = await WaitForTaskAsync(taskId, timeoutMs, cancellationToken).ConfigureAwait(false);
        return BuildResultResponse(id, ToMcpToolResult("assign_task", waitResponse));
    }

    private async Task<BridgeResponse> WaitForRefreshAsync(string requestId, int timeoutMs, CancellationToken cancellationToken)
    {
        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
        BridgeResponse? lastUnavailable = null;
        while (DateTime.UtcNow < deadlineUtc && !cancellationToken.IsCancellationRequested)
        {
            BridgeResponse statusResponse = await _bridgeClient.SendAsync(
                "refresh/status",
                new Dictionary<string, object?> { ["requestId"] = requestId },
                CreateShortBridgeTimeout(RefreshStatusBridgeTimeoutMs),
                cancellationToken).ConfigureAwait(false);

            if (!statusResponse.Success)
            {
                lastUnavailable = statusResponse;
            }
            else
            {
                Dictionary<string, object?> status = JsonUtil.AsObject(statusResponse.Result);
                string state = JsonUtil.Get(status, "state")?.ToString() ?? string.Empty;
                if (string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return BridgeResponse.FromSuccess(status);
                }

                if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    return BridgeResponse.FromError("REFRESH_FAILED", JsonUtil.Get(status, "error")?.ToString() ?? "Unity reported refresh failure.", status);
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return BridgeResponse.FromError("REFRESH_TIMEOUT", $"Unity did not complete refresh '{requestId}' within {timeoutMs} ms.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["lastBridgeError"] = lastUnavailable == null ? null : new Dictionary<string, object?>
            {
                ["code"] = lastUnavailable.ErrorCode,
                ["message"] = lastUnavailable.ErrorMessage
            }
        });
    }

    private async Task<BridgeResponse> WaitForTaskAsync(string taskId, int timeoutMs, CancellationToken cancellationToken)
    {
        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        BridgeResponse? lastUnavailable = null;
        bool bridgeWasUnavailable = false;
        while (DateTime.UtcNow < deadlineUtc && !cancellationToken.IsCancellationRequested)
        {
            BridgeResponse statusResponse = await _bridgeClient.SendAsync(
                "task/status",
                new Dictionary<string, object?> { ["taskId"] = taskId },
                CreateShortBridgeTimeout(TaskStatusBridgeTimeoutMs),
                cancellationToken).ConfigureAwait(false);

            if (!statusResponse.Success)
            {
                bridgeWasUnavailable = true;
                lastUnavailable = statusResponse;
            }
            else
            {
                Dictionary<string, object?> status = JsonUtil.AsObject(statusResponse.Result);
                string state = JsonUtil.Get(status, "status")?.ToString() ?? string.Empty;
                if (IsTerminalTaskState(state))
                {
                    return BridgeResponse.FromSuccess(SuccessEnvelope(status));
                }

                if (string.Equals(state, "not_found", StringComparison.OrdinalIgnoreCase))
                {
                    string code = bridgeWasUnavailable ? "TASK_INTERRUPTED" : "TASK_NOT_FOUND";
                    string message = bridgeWasUnavailable
                        ? $"Unity reloaded before task '{taskId}' reached a terminal state."
                        : $"Unity no longer has task '{taskId}'.";
                    return BridgeResponse.FromError(code, message, status);
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        BridgeResponse timeoutResponse = await _bridgeClient.SendAsync(
            "task/timeout",
            new Dictionary<string, object?> { ["taskId"] = taskId },
            CreateShortBridgeTimeout(TaskStatusBridgeTimeoutMs),
            cancellationToken).ConfigureAwait(false);
        if (timeoutResponse.Success)
        {
            Dictionary<string, object?> status = JsonUtil.AsObject(timeoutResponse.Result);
            string state = JsonUtil.Get(status, "status")?.ToString() ?? string.Empty;
            if (IsTerminalTaskState(state))
            {
                return BridgeResponse.FromSuccess(SuccessEnvelope(status));
            }

            if (bridgeWasUnavailable && string.Equals(state, "not_found", StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.FromError(
                    "TASK_INTERRUPTED",
                    $"Unity reloaded before task '{taskId}' reached a terminal state.",
                    status);
            }
        }

        return BridgeResponse.FromError("TASK_TIMEOUT", $"Task '{taskId}' did not complete within {timeoutMs} ms.", new Dictionary<string, object?>
        {
            ["taskId"] = taskId,
            ["lastBridgeError"] = lastUnavailable == null ? null : new Dictionary<string, object?>
            {
                ["code"] = lastUnavailable.ErrorCode,
                ["message"] = lastUnavailable.ErrorMessage
            }
        });
    }

    private static bool IsTerminalTaskState(string? state)
    {
        return string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "timed_out", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BridgeResponse> CallToolBridgeAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        return await _bridgeClient.SendAsync(
            "tools/call",
            new Dictionary<string, object?>
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            },
            timeout ?? TimeSpan.FromMilliseconds(_options.BridgeRequestTimeoutMs),
            cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan CreateShortBridgeTimeout(int timeoutMs)
    {
        return TimeSpan.FromMilliseconds(Math.Min(_options.BridgeRequestTimeoutMs, timeoutMs));
    }

    private Dictionary<string, object?> ToMcpToolResult(string toolName, BridgeResponse bridgeResponse)
    {
        Dictionary<string, object?> envelope = bridgeResponse.Success
            ? JsonUtil.AsObject(bridgeResponse.Result)
            : ErrorEnvelope(bridgeResponse.ErrorCode ?? "UNITY_UNAVAILABLE", bridgeResponse.ErrorMessage ?? "Unity Bridge is unavailable.", bridgeResponse.ErrorDetails);

        bool success = JsonUtil.Get(envelope, "success") is bool successValue && successValue;
        return new Dictionary<string, object?>
        {
            ["content"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = success
                        ? $"{toolName} completed successfully."
                        : $"{toolName} failed: {ReadEnvelopeErrorCode(envelope)} - {ReadEnvelopeErrorMessage(envelope)}"
                }
            },
            ["structuredContent"] = envelope,
            ["isError"] = !success
        };
    }

    private Dictionary<string, object?> CreateServiceState()
    {
        return new Dictionary<string, object?>
        {
            ["state"] = "running",
            ["port"] = _options.Port,
            ["endpoint"] = $"http://127.0.0.1:{_options.Port}/mcp",
            ["processId"] = Environment.ProcessId,
            ["projectPath"] = _options.ProjectPath,
            ["lastError"] = null
        };
    }

    private Dictionary<string, object?> CreateDisconnectedBridgeState(BridgeResponse bridgeResponse)
    {
        return new Dictionary<string, object?>
        {
            ["state"] = "disconnected",
            ["pipeName"] = _bridgeClient.PipeName,
            ["isConnected"] = false,
            ["lastError"] = bridgeResponse.ErrorMessage
        };
    }

    private static Dictionary<string, object?> SuccessEnvelope(object? data)
    {
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["data"] = data,
            ["logs"] = Array.Empty<object>(),
            ["warnings"] = Array.Empty<object>(),
            ["error"] = null
        };
    }

    private static Dictionary<string, object?> ErrorEnvelope(string code, string message, object? details)
    {
        return new Dictionary<string, object?>
        {
            ["success"] = false,
            ["data"] = null,
            ["logs"] = Array.Empty<object>(),
            ["warnings"] = Array.Empty<object>(),
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
                ["details"] = details
            }
        };
    }

    private static string ReadEnvelopeErrorCode(Dictionary<string, object?> envelope)
    {
        Dictionary<string, object?> error = JsonUtil.AsObject(JsonUtil.Get(envelope, "error"));
        return JsonUtil.Get(error, "code")?.ToString() ?? "ERROR";
    }

    private static string ReadEnvelopeErrorMessage(Dictionary<string, object?> envelope)
    {
        Dictionary<string, object?> error = JsonUtil.AsObject(JsonUtil.Get(envelope, "error"));
        return JsonUtil.Get(error, "message")?.ToString() ?? "Tool failed.";
    }

    private static int? ReadInt(Dictionary<string, object?> values, string key)
    {
        object? value = JsonUtil.Get(values, key);
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

        return int.TryParse(value.ToString(), out int parsed) ? parsed : null;
    }

    private static Dictionary<string, object?> BuildResultResponse(object? id, object? result)
    {
        return new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
    }

    private static Dictionary<string, object?> BuildErrorResponse(object? id, int code, string message)
    {
        return new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object? payload, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonUtil.Serialize(payload));
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static bool IsOriginAllowed(HttpListenerRequest request)
    {
        string? origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}
