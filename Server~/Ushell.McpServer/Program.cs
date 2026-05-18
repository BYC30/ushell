namespace Ushell.McpServer;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ServerOptions options = ServerOptions.Parse(args);
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        BridgeClient bridgeClient = new(options.PipeName);
        McpHttpServer server = new(options, bridgeClient);
        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            server.Stop();
        }
    }
}

internal sealed class ServerOptions
{
    public int Port { get; private init; } = 61337;
    public string PipeName { get; private init; } = "ushell-bridge";
    public string ProjectPath { get; private init; } = string.Empty;
    public int BridgeRequestTimeoutMs { get; private init; } = 30000;
    public int RefreshTimeoutMs { get; private init; } = 120000;

    public static ServerOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string item = args[index];
            if (!item.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = item.Substring(2);
            string value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return new ServerOptions
        {
            Port = ReadInt(values, "port") ?? 61337,
            PipeName = values.TryGetValue("pipe", out string? pipe) && !string.IsNullOrWhiteSpace(pipe) ? pipe : "ushell-bridge",
            ProjectPath = values.TryGetValue("project", out string? project) ? project : string.Empty,
            BridgeRequestTimeoutMs = ReadInt(values, "bridge-timeout-ms") ?? 30000,
            RefreshTimeoutMs = ReadInt(values, "refresh-timeout-ms") ?? 120000
        };
    }

    private static int? ReadInt(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : null;
    }
}
