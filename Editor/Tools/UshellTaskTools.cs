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
                Description = "Creates a human-facing Unity task, waits until it completes or reaches a step, and returns captured matching logs and invocation records.",
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
                    { "autoTriggers", OptionalArray(new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "keyword", RequiredString() },
                                    { "description", OptionalString() },
                                    { "expression", RequiredString() },
                                    { "confirm", OptionalBoolean() }
                                }
                            },
                            { "required", new[] { "keyword", "expression" } }
                        })
                    },
                    { "steps", OptionalArray(new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "stepId", OptionalString() },
                                    { "keyword", RequiredString() },
                                    { "description", OptionalString() }
                                }
                            },
                            { "required", new[] { "keyword" } }
                        })
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
                        List<UshellTaskAutoTrigger> autoTriggers = ReadAutoTriggers(arguments);
                        List<UshellTaskStep> steps = ReadSteps(arguments);

                        string taskId = UshellTaskStore.CreateTask(description, logKeyword, completionKeyword, buttons, autoTriggers, steps);
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

        public static UshellToolDefinition CreateContinueTaskTool()
        {
            return new UshellToolDefinition
            {
                Name = "continue_task",
                Description = "Reconfigures a step-reached task and waits for its next step or completion.",
                InputSchema = SchemaForObject(new Dictionary<string, object>
                {
                    { "taskId", RequiredString() },
                    { "description", OptionalString() },
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
                    { "autoTriggers", OptionalArray(new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "keyword", RequiredString() },
                                    { "description", OptionalString() },
                                    { "expression", RequiredString() },
                                    { "confirm", OptionalBoolean() }
                                }
                            },
                            { "required", new[] { "keyword", "expression" } }
                        })
                    },
                    { "steps", OptionalArray(new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "stepId", OptionalString() },
                                    { "keyword", RequiredString() },
                                    { "description", OptionalString() }
                                }
                            },
                            { "required", new[] { "keyword" } }
                        })
                    },
                    { "timeoutMs", OptionalNumber() }
                }, "taskId", "logKeyword", "completionKeyword"),
                Handler = arguments =>
                {
                    try
                    {
                        string taskId = UshellArgumentReader.RequireString(arguments, "taskId");
                        string description = UshellArgumentReader.GetString(arguments, "description");
                        string logKeyword = UshellArgumentReader.RequireString(arguments, "logKeyword");
                        string completionKeyword = UshellArgumentReader.RequireString(arguments, "completionKeyword");
                        int timeoutMs = UshellArgumentReader.GetInt(arguments, "timeoutMs") ?? DefaultAssignTaskTimeoutMs;
                        List<UshellTaskButton> buttons = ReadButtons(arguments);
                        List<UshellTaskAutoTrigger> autoTriggers = ReadAutoTriggers(arguments);
                        List<UshellTaskStep> steps = ReadSteps(arguments);

                        UshellTaskStore.ContinueTask(taskId, description, logKeyword, completionKeyword, buttons, autoTriggers, steps);
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
                        return UshellToolEnvelope.FromError("INVALID_TASK_TRANSITION", exception.Message);
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

        private static List<UshellTaskAutoTrigger> ReadAutoTriggers(Dictionary<string, object> arguments)
        {
            object rawAutoTrigger = UshellArgumentReader.GetValue(arguments, "autoTrigger");
            object rawAutoTriggers = UshellArgumentReader.GetValue(arguments, "autoTriggers");
            if (rawAutoTrigger != null && rawAutoTriggers != null)
            {
                throw new InvalidOperationException("Use either 'autoTrigger' or 'autoTriggers', not both.");
            }

            List<UshellTaskAutoTrigger> result = new List<UshellTaskAutoTrigger>();
            if (rawAutoTrigger != null)
            {
                Dictionary<string, object> legacyTrigger = rawAutoTrigger as Dictionary<string, object>;
                if (legacyTrigger == null)
                {
                    throw new InvalidOperationException("Argument 'autoTrigger' must be an object.");
                }

                result.Add(ReadAutoTrigger(legacyTrigger, "autoTrigger"));
                return result;
            }

            List<object> triggers = ReadObjectArray(rawAutoTriggers, "autoTriggers");
            for (int index = 0; index < triggers.Count; index++)
            {
                Dictionary<string, object> trigger = triggers[index] as Dictionary<string, object>;
                if (trigger == null)
                {
                    throw new InvalidOperationException($"Argument 'autoTriggers[{index}]' must be an object.");
                }

                result.Add(ReadAutoTrigger(trigger, $"autoTriggers[{index}]"));
            }

            return result;
        }

        private static UshellTaskAutoTrigger ReadAutoTrigger(Dictionary<string, object> source, string argumentName)
        {
            try
            {
                return new UshellTaskAutoTrigger
                {
                    Keyword = UshellArgumentReader.RequireString(source, "keyword"),
                    Description = UshellArgumentReader.GetString(source, "description"),
                    Expression = UshellArgumentReader.RequireString(source, "expression"),
                    Confirm = UshellArgumentReader.GetBool(source, "confirm") ?? false
                };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Invalid '{argumentName}': {exception.Message}");
            }
        }

        private static List<UshellTaskStep> ReadSteps(Dictionary<string, object> arguments)
        {
            List<object> steps = ReadObjectArray(UshellArgumentReader.GetValue(arguments, "steps"), "steps");
            List<UshellTaskStep> result = new List<UshellTaskStep>();
            for (int index = 0; index < steps.Count; index++)
            {
                Dictionary<string, object> step = steps[index] as Dictionary<string, object>;
                if (step == null)
                {
                    throw new InvalidOperationException($"Argument 'steps[{index}]' must be an object.");
                }

                result.Add(new UshellTaskStep
                {
                    Id = UshellArgumentReader.GetString(step, "stepId"),
                    Keyword = UshellArgumentReader.RequireString(step, "keyword"),
                    Description = UshellArgumentReader.GetString(step, "description")
                });
            }

            return result;
        }

        private static List<object> ReadObjectArray(object value, string argumentName)
        {
            if (value == null)
            {
                return new List<object>();
            }

            if (value is List<object> list)
            {
                return list;
            }

            if (value is object[] array)
            {
                return array.ToList();
            }

            throw new InvalidOperationException($"Argument '{argumentName}' must be an array.");
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
