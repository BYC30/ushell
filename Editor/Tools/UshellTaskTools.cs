using System;
using System.Collections.Generic;
using System.Linq;

namespace Ushell.Editor
{
    public static class UshellTaskTools
    {
        private const int DefaultAssignTaskTimeoutMs = 1800000;

        public static UshellToolDefinition CreateAssignTaskTool()
        {
            return new UshellToolDefinition
            {
                Name = "assign_task",
                Description = "Creates a human-facing Unity task, waits until it completes, and returns captured matching logs and button invocation records.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "description", RequiredString() },
                    { "logKeyword", RequiredString() },
                    { "completionKeyword", RequiredString() },
                    { "buttons", OptionalArray(new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "label", RequiredString() },
                                    { "description", OptionalString() },
                                    { "expression", RequiredString() }
                                }
                            },
                            { "required", new[] { "label", "expression" } }
                        })
                    },
                    { "autoTrigger", OptionalObject(new Dictionary<string, object>
                        {
                            { "keyword", RequiredString() },
                            { "description", OptionalString() },
                            { "expression", RequiredString() },
                            { "confirm", OptionalBoolean() }
                        }, "keyword", "expression")
                    },
                    { "timeoutMs", OptionalNumber() }
                }, "description", "logKeyword", "completionKeyword"),
                Handler = arguments =>
                {
                    try
                    {
                        string description = UshellArgumentReader.RequireString(arguments, "description");
                        string logKeyword = UshellArgumentReader.RequireString(arguments, "logKeyword");
                        string completionKeyword = UshellArgumentReader.RequireString(arguments, "completionKeyword");
                        int timeoutMs = UshellArgumentReader.GetInt(arguments, "timeoutMs") ?? DefaultAssignTaskTimeoutMs;
                        List<UshellTaskButton> buttons = ReadButtons(arguments);
                        UshellTaskAutoTrigger autoTrigger = ReadAutoTrigger(arguments);

                        string taskId = UshellTaskStore.CreateTask(description, logKeyword, completionKeyword, buttons, autoTrigger);
                        return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                        {
                            { "accepted", true },
                            { "taskId", taskId },
                            { "status", "active" },
                            { "timeoutMs", timeoutMs }
                        });
                    }
                    catch (Exception exception)
                    {
                        return UshellToolEnvelope.FromError("INVALID_ARGUMENT", exception.Message);
                    }
                }
            };
        }

        public static UshellToolDefinition CreateListTasksTool()
        {
            return new UshellToolDefinition
            {
                Name = "list_tasks",
                Description = "Lists AI task summaries from the current Unity Editor process memory.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "includeCompleted", OptionalBoolean() },
                    { "limit", OptionalNumber() }
                }),
                Handler = arguments =>
                {
                    bool includeCompleted = UshellArgumentReader.GetBool(arguments, "includeCompleted") ?? true;
                    int limit = UshellArgumentReader.GetInt(arguments, "limit") ?? 50;
                    List<Dictionary<string, object>> tasks = UshellTaskStore
                        .Snapshot(includeCompleted, limit)
                        .Select(task => task.ToSummaryDictionary())
                        .ToList();

                    return UshellToolEnvelope.FromSuccess(new Dictionary<string, object>
                    {
                        { "tasks", tasks }
                    });
                }
            };
        }

        public static UshellToolDefinition CreateGetTaskTool()
        {
            return new UshellToolDefinition
            {
                Name = "get_task",
                Description = "Returns the full AI task record for a task id.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "taskId", RequiredString() }
                }, "taskId"),
                Handler = arguments =>
                {
                    string taskId = UshellArgumentReader.RequireString(arguments, "taskId");
                    UshellTaskRecord record = UshellTaskStore.GetTask(taskId);
                    if (record == null)
                    {
                        return UshellToolEnvelope.FromError("TASK_NOT_FOUND", $"Unknown task '{taskId}'.");
                    }

                    return UshellToolEnvelope.FromSuccess(record.ToDictionary());
                }
            };
        }

        private static List<UshellTaskButton> ReadButtons(Dictionary<string, object> arguments)
        {
            object rawButtons = UshellArgumentReader.GetValue(arguments, "buttons");
            List<UshellTaskButton> buttons = new List<UshellTaskButton>();
            if (rawButtons == null)
            {
                return buttons;
            }

            List<object> list = rawButtons as List<object>;
            if (list == null)
            {
                object[] array = rawButtons as object[];
                if (array != null)
                {
                    list = array.ToList();
                }
            }

            if (list == null)
            {
                throw new InvalidOperationException("Argument 'buttons' must be an array.");
            }

            for (int index = 0; index < list.Count; index++)
            {
                Dictionary<string, object> rawButton = list[index] as Dictionary<string, object>;
                if (rawButton == null)
                {
                    throw new InvalidOperationException($"Argument 'buttons[{index}]' must be an object.");
                }

                string label = UshellArgumentReader.RequireString(rawButton, "label");
                string expression = UshellArgumentReader.RequireString(rawButton, "expression");
                buttons.Add(new UshellTaskButton
                {
                    Id = $"button-{index + 1}",
                    Label = label,
                    Description = UshellArgumentReader.GetString(rawButton, "description"),
                    Expression = expression
                });
            }

            return buttons;
        }

        private static UshellTaskAutoTrigger ReadAutoTrigger(Dictionary<string, object> arguments)
        {
            object rawAutoTrigger = UshellArgumentReader.GetValue(arguments, "autoTrigger");
            if (rawAutoTrigger == null)
            {
                return null;
            }

            Dictionary<string, object> autoTrigger = rawAutoTrigger as Dictionary<string, object>;
            if (autoTrigger == null)
            {
                throw new InvalidOperationException("Argument 'autoTrigger' must be an object.");
            }

            return new UshellTaskAutoTrigger
            {
                Keyword = UshellArgumentReader.RequireString(autoTrigger, "keyword"),
                Description = UshellArgumentReader.GetString(autoTrigger, "description"),
                Expression = UshellArgumentReader.RequireString(autoTrigger, "expression"),
                Confirm = UshellArgumentReader.GetBool(autoTrigger, "confirm") ?? false
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

        private static Dictionary<string, object> OptionalObject(Dictionary<string, object> properties, params string[] required)
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

        private static Dictionary<string, object> OptionalArray(Dictionary<string, object> items)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", items }
            };
        }
    }
}
