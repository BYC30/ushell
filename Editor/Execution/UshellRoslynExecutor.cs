using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Mono.CSharp;
using UnityEngine;

namespace Ushell.Editor
{
    public sealed class UshellCodeExecutionResult
    {
        public bool Success;
        public string ErrorCode;
        public string ErrorMessage;
        public object Details;
        public object ReturnValue;
        public long DurationMs;
        public List<Dictionary<string, object>> Logs = new List<Dictionary<string, object>>();
        public List<string> Warnings = new List<string>();
    }

    public static class UshellEvaluatorGlobals
    {
        [ThreadStatic]
        public static UshellContext ctx;

        public static void SetCurrentContext(UshellContext context)
        {
            ctx = context;
        }
    }

    internal sealed class UshellMonoEvaluatorSession
    {
        private readonly object _syncRoot = new object();

        private Evaluator _evaluator;
        private bool _initialized;
        private string _initializationError;
        private int _completionHandle;
        private string[] _completions = Array.Empty<string>();

        public void WarmUp()
        {
            EnsureInitialized();
        }

        public string[] GetCompletionSnapshot()
        {
            lock (_syncRoot)
            {
                return _completions?.ToArray() ?? Array.Empty<string>();
            }
        }

        public void RequestCompletions(string input)
        {
            EnsureInitialized();

            int handle = Interlocked.Increment(ref _completionHandle);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string[] completions = BuildCompletions(input);
                lock (_syncRoot)
                {
                    if (handle == _completionHandle)
                    {
                        _completions = completions;
                    }
                }
            });
        }

        public object Evaluate(string command)
        {
            EnsureInitialized();
            string initializationError = GetInitializationError();
            if (!string.IsNullOrWhiteSpace(initializationError))
            {
                throw new InvalidOperationException(initializationError);
            }

            string normalized = NormalizeCommand(command);
            lock (_syncRoot)
            {
                if (_evaluator == null)
                {
                    throw new InvalidOperationException("Mono evaluator is unavailable.");
                }

                string workingCommand = normalized;
                CompiledMethod compiled = _evaluator.Compile(workingCommand);
                if (compiled == null && !workingCommand.TrimEnd().EndsWith(";") && !workingCommand.TrimEnd().EndsWith("}"))
                {
                    workingCommand += ";";
                    compiled = _evaluator.Compile(workingCommand);
                }

                if (compiled == null)
                {
                    throw new InvalidOperationException("Compilation failed.");
                }

                object result = null;
                compiled(ref result);
                return result;
            }
        }

        private string[] BuildCompletions(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<string>();
            }

            string initializationError = GetInitializationError();
            if (!string.IsNullOrWhiteSpace(initializationError))
            {
                return Array.Empty<string>();
            }

            lock (_syncRoot)
            {
                if (_evaluator == null)
                {
                    return Array.Empty<string>();
                }

                string prefix;
                string[] rawCompletions = _evaluator.GetCompletions(input, out prefix);
                if (rawCompletions == null || rawCompletions.Length == 0)
                {
                    return Array.Empty<string>();
                }

                string[] completions = new string[rawCompletions.Length];
                for (int index = 0; index < rawCompletions.Length; index++)
                {
                    completions[index] = input + rawCompletions[index];
                }

                if (completions.Length == 1 && string.Equals(completions[0].Trim(), input.Trim(), StringComparison.Ordinal))
                {
                    return Array.Empty<string>();
                }

                return completions;
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                try
                {
                    _evaluator = new Evaluator(new CompilerContext(new CompilerSettings(), new ConsoleReportPrinter()));
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(item => item != null))
                    {
                        try
                        {
                            _evaluator.ReferenceAssembly(assembly);
                        }
                        catch
                        {
                            // Ignore assemblies the evaluator cannot consume.
                        }
                    }

                    _evaluator.Run("using System;");
                    _evaluator.Run("using System.Linq;");
                    _evaluator.Run("using System.Collections.Generic;");
                    _evaluator.Run("using UnityEngine;");
                    _evaluator.Run("using UnityEditor;");
                    _evaluator.Run("using Ushell.Editor;");
                    _evaluator.Run("using Ushell.Runtime;");
                    _evaluator.Run("using static Ushell.Editor.UshellEvaluatorGlobals;");

                    _initializationError = null;
                }
                catch (Exception exception)
                {
                    _evaluator = null;
                    _initializationError = exception.ToString();
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        private string GetInitializationError()
        {
            EnsureInitialized();
            lock (_syncRoot)
            {
                return _initializationError;
            }
        }

        private static string NormalizeCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException("Command is required.");
            }

            return command.Trim();
        }
    }

    public static class UshellRoslynExecutor
    {
        private static readonly UshellMonoEvaluatorSession Session = new UshellMonoEvaluatorSession();

        public static void WarmUp()
        {
            Session.WarmUp();
        }

        public static void RequestCompletions(string input)
        {
            Session.RequestCompletions(input);
        }

        public static string[] GetCompletionSnapshot()
        {
            return Session.GetCompletionSnapshot();
        }

        public static UshellCodeExecutionResult Execute(string expression, bool captureLogs, int timeoutMs)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            UshellContext context = new UshellContext(CancellationToken.None);
            List<Dictionary<string, object>> capturedLogs = new List<Dictionary<string, object>>();
            object logSync = new object();

            Application.LogCallback callback = null;
            if (captureLogs)
            {
                callback = (condition, stackTrace, type) =>
                {
                    Dictionary<string, object> entry = new Dictionary<string, object>
                    {
                        { "type", type.ToString() },
                        { "message", condition }
                    };

                    if (!string.IsNullOrWhiteSpace(stackTrace))
                    {
                        entry["stackTrace"] = stackTrace;
                    }

                    lock (logSync)
                    {
                        capturedLogs.Add(entry);
                    }
                };

                Application.logMessageReceivedThreaded += callback;
            }

            UshellEvaluatorGlobals.SetCurrentContext(context);
            try
            {
                object returnValue = Session.Evaluate(expression);
                stopwatch.Stop();

                UshellCodeExecutionResult result = new UshellCodeExecutionResult
                {
                    Success = true,
                    ReturnValue = returnValue,
                    DurationMs = stopwatch.ElapsedMilliseconds
                };

                if (captureLogs)
                {
                    lock (logSync)
                    {
                        result.Logs.AddRange(capturedLogs);
                    }
                }

                if (timeoutMs > 0 && result.DurationMs > timeoutMs)
                {
                    result.Warnings.Add($"Execution exceeded the requested timeout of {timeoutMs}ms, but Mono evaluator execution cannot be interrupted safely.");
                }

                return result;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                UshellCodeExecutionResult failed = Failed("COMPILE_ERROR", exception.Message, exception.ToString());
                failed.DurationMs = stopwatch.ElapsedMilliseconds;
                if (captureLogs)
                {
                    lock (logSync)
                    {
                        failed.Logs.AddRange(capturedLogs);
                    }
                }

                return failed;
            }
            finally
            {
                UshellEvaluatorGlobals.SetCurrentContext(null);
                if (callback != null)
                {
                    Application.logMessageReceivedThreaded -= callback;
                }
            }
        }

        private static UshellCodeExecutionResult Failed(string code, string message, object details = null)
        {
            return new UshellCodeExecutionResult
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = message,
                Details = details
            };
        }
    }
}
