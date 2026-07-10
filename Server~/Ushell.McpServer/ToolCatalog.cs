namespace Ushell.McpServer;

internal static class ToolCatalog
{
    public static IReadOnlyList<Dictionary<string, object?>> CreateDefaultTools()
    {
        return new[]
        {
            Tool("health_check", "Returns the current Unity Editor, compile, PlayMode, Bridge, and MCP service state.", Schema()),
            Tool("get_logs", "Returns captured Unity log records with optional filtering by type, sequence, keyword, and regex, and can clear logs after reading.", Schema(new()
            {
                ["logType"] = String(),
                ["sinceSequence"] = Number(),
                ["keyword"] = String(),
                ["regex"] = String(),
                ["limit"] = Number(),
                ["clearAfterRead"] = Bool()
            })),
            Tool("clear_logs", "Clears the ushell captured log buffer and attempts to clear the Unity console.", Schema()),
            Tool("enter_playmode", "Enters PlayMode if the Editor is currently idle.", Schema()),
            Tool("exit_playmode", "Exits PlayMode if the Editor is currently playing.", Schema()),
            Tool("exec_expr", "Executes a C# snippet inside the Unity Editor and returns the echoed input together with its result.", Schema(new()
            {
                ["expression"] = RequiredString(),
                ["timeoutMs"] = Number(),
                ["captureLogs"] = Bool(),
                ["confirm"] = Bool()
            }, "expression")),
            Tool("capture_screenshot", "Captures the current GameView into a PNG file under an allowed output path.", Schema(new()
            {
                ["outputPath"] = String(),
                ["confirm"] = Bool()
            })),
            Tool("build_project", "Builds the current project using enabled EditorBuildSettings scenes.", Schema(new()
            {
                ["buildProfile"] = String(),
                ["target"] = String(),
                ["development"] = Bool(),
                ["outputPath"] = String(),
                ["confirm"] = Bool()
            })),
            Tool("get_build_status", "Returns the most recent build summary captured by ushell.", Schema()),
            Tool("refresh_assets", "Refreshes the Unity AssetDatabase so externally modified files are reimported and script compilation can finish while the MCP server remains alive.", Schema(new()
            {
                ["forceSynchronousImport"] = Bool(),
                ["timeoutMs"] = Number()
            })),
            Tool("runtime_invoke", "Invokes a registered runtime action while the Unity player loop is active.", Schema(new()
            {
                ["name"] = RequiredString(),
                ["payload"] = Object()
            }, "name")),
            Tool("assign_task", "Creates a human-facing Unity task, waits until it completes or reaches a step, and returns captured matching logs and invocation records.", Schema(new()
            {
                ["description"] = RequiredString(),
                ["logKeyword"] = RequiredString(),
                ["completionKeyword"] = RequiredString(),
                ["buttons"] = Array(Object(new()
                {
                    ["label"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString()
                }, "label", "expression")),
                ["autoTrigger"] = Object(new()
                {
                    ["keyword"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString(),
                    ["confirm"] = Bool()
                }, "keyword", "expression"),
                ["autoTriggers"] = Array(Object(new()
                {
                    ["keyword"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString(),
                    ["confirm"] = Bool()
                }, "keyword", "expression")),
                ["steps"] = Array(Object(new()
                {
                    ["stepId"] = String(),
                    ["keyword"] = RequiredString(),
                    ["description"] = String()
                }, "keyword")),
                ["timeoutMs"] = Number()
            }, "description", "logKeyword", "completionKeyword")),
            Tool("continue_task", "Reconfigures a step-reached task and waits for its next step or completion.", Schema(new()
            {
                ["taskId"] = RequiredString(),
                ["description"] = String(),
                ["logKeyword"] = RequiredString(),
                ["completionKeyword"] = RequiredString(),
                ["buttons"] = Array(Object(new()
                {
                    ["label"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString()
                }, "label", "expression")),
                ["autoTrigger"] = Object(new()
                {
                    ["keyword"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString(),
                    ["confirm"] = Bool()
                }, "keyword", "expression"),
                ["autoTriggers"] = Array(Object(new()
                {
                    ["keyword"] = RequiredString(),
                    ["description"] = String(),
                    ["expression"] = RequiredString(),
                    ["confirm"] = Bool()
                }, "keyword", "expression")),
                ["steps"] = Array(Object(new()
                {
                    ["stepId"] = String(),
                    ["keyword"] = RequiredString(),
                    ["description"] = String()
                }, "keyword")),
                ["timeoutMs"] = Number()
            }, "taskId", "logKeyword", "completionKeyword")),
            Tool("list_tasks", "Lists AI task summaries from the current Unity Editor process memory.", Schema(new()
            {
                ["includeCompleted"] = Bool(),
                ["limit"] = Number()
            })),
            Tool("get_task", "Returns the full AI task record for a task id.", Schema(new()
            {
                ["taskId"] = RequiredString()
            }, "taskId"))
        };
    }

    private static Dictionary<string, object?> Tool(string name, string description, Dictionary<string, object?> inputSchema)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    private static Dictionary<string, object?> Schema(Dictionary<string, object?>? properties = null, params string[] required)
    {
        Dictionary<string, object?> schema = new(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = properties ?? new Dictionary<string, object?>()
        };

        if (required.Length > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object?> String()
    {
        return new Dictionary<string, object?> { ["type"] = "string" };
    }

    private static Dictionary<string, object?> RequiredString()
    {
        return new Dictionary<string, object?> { ["type"] = "string", ["minLength"] = 1 };
    }

    private static Dictionary<string, object?> Number()
    {
        return new Dictionary<string, object?> { ["type"] = "number" };
    }

    private static Dictionary<string, object?> Bool()
    {
        return new Dictionary<string, object?> { ["type"] = "boolean" };
    }

    private static Dictionary<string, object?> Object()
    {
        return new Dictionary<string, object?> { ["type"] = "object" };
    }

    private static Dictionary<string, object?> Object(Dictionary<string, object?> properties, params string[] required)
    {
        Dictionary<string, object?> schema = Object();
        schema["properties"] = properties;
        if (required.Length > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object?> Array(Dictionary<string, object?> items)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["items"] = items
        };
    }
}
