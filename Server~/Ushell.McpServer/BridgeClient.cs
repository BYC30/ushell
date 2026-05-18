using System.IO.Pipes;
using System.Text;

namespace Ushell.McpServer;

internal sealed class BridgeClient
{
    private const int ConnectTimeoutMs = 1000;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public BridgeClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    public string PipeName => _pipeName;

    public async Task<BridgeResponse> SendAsync(string method, Dictionary<string, object?>? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool lockTaken = false;
        try
        {
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            await _sendLock.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            lockTaken = true;

            using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using (CancellationTokenSource connectSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token))
            {
                connectSource.CancelAfter(ConnectTimeoutMs);
                await pipe.ConnectAsync(connectSource.Token).ConfigureAwait(false);
            }

            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);

            Dictionary<string, object?> request = new(StringComparer.Ordinal)
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };

            await writer.WriteLineAsync(JsonUtil.Serialize(request)).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return BridgeResponse.FromError("UNITY_UNAVAILABLE", "Unity Bridge closed the pipe without returning a response.");
            }

            Dictionary<string, object?> response = JsonUtil.AsObject(JsonUtil.Deserialize(line));
            bool success = JsonUtil.Get(response, "success") is bool successValue && successValue;
            if (success)
            {
                return BridgeResponse.FromSuccess(JsonUtil.Get(response, "result"));
            }

            Dictionary<string, object?> error = JsonUtil.AsObject(JsonUtil.Get(response, "error"));
            string code = JsonUtil.Get(error, "code")?.ToString() ?? "UNITY_ERROR";
            string message = JsonUtil.Get(error, "message")?.ToString() ?? "Unity Bridge returned an error.";
            return BridgeResponse.FromError(code, message, JsonUtil.Get(error, "details"));
        }
        catch (OperationCanceledException)
        {
            return BridgeResponse.FromError("UNITY_UNAVAILABLE", "Unity Bridge did not respond before the pipe timeout.");
        }
        catch (Exception exception) when (exception is IOException || exception is TimeoutException || exception is UnauthorizedAccessException)
        {
            return BridgeResponse.FromError("UNITY_UNAVAILABLE", exception.Message);
        }
        finally
        {
            if (lockTaken)
            {
                _sendLock.Release();
            }
        }
    }
}

internal sealed class BridgeResponse
{
    public bool Success { get; private init; }
    public object? Result { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }
    public object? ErrorDetails { get; private init; }

    public static BridgeResponse FromSuccess(object? result)
    {
        return new BridgeResponse
        {
            Success = true,
            Result = result
        };
    }

    public static BridgeResponse FromError(string code, string message, object? details = null)
    {
        return new BridgeResponse
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
            ErrorDetails = details
        };
    }
}
