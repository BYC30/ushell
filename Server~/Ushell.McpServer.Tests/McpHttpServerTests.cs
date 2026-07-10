using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ushell.McpServer;
using Xunit;

namespace Ushell.McpServer.Tests;

public sealed class McpHttpServerTests
{
    [Fact]
    public async Task AssignTask_DoesNotBlockHealthCheck()
    {
        int port = GetAvailablePort();
        string pipeName = "ushell-test-" + Guid.NewGuid().ToString("N");
        await using FakeBridgeServer fakeBridge = new(pipeName, TimeSpan.FromSeconds(2));
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        McpHttpServer server = CreateServer(port, pipeName);
        Task serverTask = server.RunAsync(shutdown.Token);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/") };

        try
        {
            await WaitUntilReadyAsync(client, shutdown.Token);
            Task<JsonDocument> taskRequest = PostAsync(client, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "assign_task",
                    arguments = new
                    {
                        description = "integration test",
                        logKeyword = "test",
                        completionKeyword = "done",
                        timeoutMs = 10000
                    }
                }
            }, shutdown.Token);

            await Task.Delay(500, shutdown.Token);
            Stopwatch stopwatch = Stopwatch.StartNew();
            using JsonDocument healthResponse = await PostAsync(client, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "health_check", arguments = new { } }
            }, shutdown.Token);
            stopwatch.Stop();

            Assert.True(ReadStructuredSuccess(healthResponse));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Health check took {stopwatch.Elapsed} while assign_task was active.");

            using JsonDocument taskResponse = await taskRequest;
            Assert.True(ReadStructuredSuccess(taskResponse));
            Assert.Equal("completed", taskResponse.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("data")
                .GetProperty("status")
                .GetString());
        }
        finally
        {
            server.Stop();
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task AssignTask_TimeoutTransitionsUnityTaskToTimedOut()
    {
        int port = GetAvailablePort();
        string pipeName = "ushell-test-" + Guid.NewGuid().ToString("N");
        await using FakeBridgeServer fakeBridge = new(pipeName, TimeSpan.FromMinutes(1));
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        McpHttpServer server = CreateServer(port, pipeName);
        Task serverTask = server.RunAsync(shutdown.Token);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/") };

        try
        {
            await WaitUntilReadyAsync(client, shutdown.Token);
            using JsonDocument response = await PostAsync(client, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "assign_task",
                    arguments = new
                    {
                        description = "timeout test",
                        logKeyword = "test",
                        completionKeyword = "done",
                        timeoutMs = 500
                    }
                }
            }, shutdown.Token);

            Assert.True(ReadStructuredSuccess(response));
            Assert.Equal("timed_out", response.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("data")
                .GetProperty("status")
                .GetString());
        }
        finally
        {
            server.Stop();
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ToolsList_WhenBridgeIsUnavailable_ReturnsFallbackCatalog()
    {
        int port = GetAvailablePort();
        string pipeName = "ushell-test-missing-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        McpHttpServer server = CreateServer(port, pipeName);
        Task serverTask = server.RunAsync(shutdown.Token);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/") };

        try
        {
            await WaitUntilReadyAsync(client, shutdown.Token);
            using JsonDocument response = await PostAsync(client, new { jsonrpc = "2.0", id = 1, method = "tools/list" }, shutdown.Token);
            JsonElement tools = response.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "assign_task");
        }
        finally
        {
            server.Stop();
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task SpoofedLocalhostOrigin_IsRejected()
    {
        int port = GetAvailablePort();
        string pipeName = "ushell-test-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        McpHttpServer server = CreateServer(port, pipeName);
        Task serverTask = server.RunAsync(shutdown.Token);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/") };

        try
        {
            await WaitUntilReadyAsync(client, shutdown.Token);
            using HttpRequestMessage request = CreateRequest(new { jsonrpc = "2.0", id = 1, method = "ping" });
            request.Headers.Add("Origin", "http://localhost.attacker.invalid");
            using HttpResponseMessage response = await client.SendAsync(request, shutdown.Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            server.Stop();
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static McpHttpServer CreateServer(int port, string pipeName)
    {
        ServerOptions options = ServerOptions.Parse(new[]
        {
            "--port", port.ToString(),
            "--pipe", pipeName,
            "--project", Directory.GetCurrentDirectory()
        });
        return new McpHttpServer(options, new BridgeClient(pipeName));
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using JsonDocument response = await PostAsync(client, new { jsonrpc = "2.0", id = 0, method = "ping" }, cancellationToken);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException("MCP HTTP server did not become ready.");
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, object payload, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(payload);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(object payload)
    {
        return new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    private static bool ReadStructuredSuccess(JsonDocument response)
    {
        return response.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("success")
            .GetBoolean();
    }

    private static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeBridgeServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly string _pipeName;
        private readonly DateTime _completeAtUtc;
        private readonly Task _listenTask;

        public FakeBridgeServer(string pipeName, TimeSpan completionDelay)
        {
            _pipeName = pipeName;
            _completeAtUtc = DateTime.UtcNow.Add(completionDelay);
            _listenTask = ListenAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }

        private async Task ListenAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, true);
                await using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
                string? line = await reader.ReadLineAsync(_shutdown.Token);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument request = JsonDocument.Parse(line);
                object result = CreateResult(request.RootElement);
                string response = JsonSerializer.Serialize(new { success = true, result, error = (object?)null });
                await writer.WriteLineAsync(response);
            }
        }

        private object CreateResult(JsonElement request)
        {
            string? method = request.GetProperty("method").GetString();
            if (method == "task/status")
            {
                string status = DateTime.UtcNow >= _completeAtUtc ? "completed" : "active";
                return new
                {
                    taskId = "task-test",
                    status,
                    reason = status == "completed" ? "manual" : null,
                    capturedLogs = Array.Empty<object>(),
                    buttonInvocations = Array.Empty<object>(),
                    autoTriggerInvocations = Array.Empty<object>()
                };
            }

            if (method == "task/timeout")
            {
                return new { taskId = "task-test", status = "timed_out", reason = "timeout" };
            }

            if (method == "tools/call")
            {
                string? toolName = request.GetProperty("params").GetProperty("name").GetString();
                object data = toolName == "assign_task"
                    ? new { accepted = true, taskId = "task-test", status = "active", timeoutMs = 10000 }
                    : new { bridgeState = new { state = "connected" } };
                return new { success = true, data, logs = Array.Empty<object>(), warnings = Array.Empty<object>(), error = (object?)null };
            }

            return new { };
        }
    }
}
