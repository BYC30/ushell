using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

    internal sealed class UshellEvaluationOutput
    {
        public object ReturnValue;
        public List<string> Warnings = new List<string>();
    }

    internal sealed class UshellCompileDiagnostic
    {
        public string Severity;
        public string Code;
        public string Message;
        public string Location;
        public string FormattedMessage;
    }

    internal sealed class UshellCompilationException : InvalidOperationException
    {
        public IReadOnlyList<UshellCompileDiagnostic> Diagnostics { get; }
        public string DetailedMessage { get; }

        public UshellCompilationException(IReadOnlyList<UshellCompileDiagnostic> diagnostics)
            : base(CreateSummaryMessage(diagnostics))
        {
            Diagnostics = diagnostics ?? Array.Empty<UshellCompileDiagnostic>();
            DetailedMessage = CreateDetailedMessage(Diagnostics);
        }

        private static string CreateSummaryMessage(IReadOnlyList<UshellCompileDiagnostic> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return "Compilation failed.";
            }

            UshellCompileDiagnostic firstError = diagnostics.FirstOrDefault(item => string.Equals(item?.Severity, "error", StringComparison.OrdinalIgnoreCase));
            if (firstError == null)
            {
                firstError = diagnostics[0];
            }

            int errorCount = diagnostics.Count(item => string.Equals(item?.Severity, "error", StringComparison.OrdinalIgnoreCase));
            if (errorCount <= 1)
            {
                return firstError.FormattedMessage ?? "Compilation failed.";
            }

            return $"Compilation failed with {errorCount} errors. First error: {firstError.FormattedMessage}";
        }

        private static string CreateDetailedMessage(IReadOnlyList<UshellCompileDiagnostic> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return "Compilation failed.";
            }

            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < diagnostics.Count; index++)
            {
                UshellCompileDiagnostic diagnostic = diagnostics[index];
                if (diagnostic == null || string.IsNullOrWhiteSpace(diagnostic.FormattedMessage))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(diagnostic.FormattedMessage);
            }

            return builder.Length == 0 ? "Compilation failed." : builder.ToString();
        }
    }

    internal sealed class UshellCollectingReportPrinter : ConsoleReportPrinter
    {
        private readonly StringWriter _writer;
        private readonly List<UshellCompileDiagnostic> _diagnostics = new List<UshellCompileDiagnostic>();

        public UshellCollectingReportPrinter()
            : this(new StringWriter())
        {
        }

        private UshellCollectingReportPrinter(StringWriter writer)
            : base(writer)
        {
            _writer = writer;
        }

        public override void Print(AbstractMessage message, bool showFullPath)
        {
            base.Print(message, showFullPath);
            _diagnostics.Add(new UshellCompileDiagnostic
            {
                Severity = message.IsWarning ? "warning" : "error",
                Code = $"CS{message.Code:D4}",
                Message = message.Text,
                Location = FormatLocation(message.Location),
                FormattedMessage = FormatDiagnostic(message)
            });
        }

        public void ClearDiagnostics()
        {
            _diagnostics.Clear();
            _writer.GetStringBuilder().Clear();
            Reset();
        }

        public List<UshellCompileDiagnostic> GetDiagnosticsSnapshot()
        {
            return _diagnostics.Select(item => new UshellCompileDiagnostic
            {
                Severity = item.Severity,
                Code = item.Code,
                Message = item.Message,
                Location = item.Location,
                FormattedMessage = item.FormattedMessage
            }).ToList();
        }

        public List<string> GetWarningMessages()
        {
            return _diagnostics
                .Where(item => item != null && string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.FormattedMessage)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public bool HasErrors()
        {
            return _diagnostics.Any(item => item != null && string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatDiagnostic(AbstractMessage message)
        {
            string code = $"CS{message.Code:D4}";
            string location = FormatLocation(message.Location);
            string text = string.IsNullOrWhiteSpace(message.Text) ? "Compilation failed." : message.Text.Trim();

            if (string.IsNullOrWhiteSpace(location))
            {
                return $"{code}: {text}";
            }

            return $"{code} {location}: {text}";
        }

        private static string FormatLocation(Location location)
        {
            string value = location.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    internal sealed class UshellMonoEvaluatorSession
    {
        private readonly object _syncRoot = new object();

        private Evaluator _evaluator;
        private UshellCollectingReportPrinter _reportPrinter;
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

        public UshellEvaluationOutput Evaluate(string command)
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
                ResetCompilationDiagnostics();
                CompiledMethod compiled = _evaluator.Compile(workingCommand);
                if (compiled == null && !workingCommand.TrimEnd().EndsWith(";") && !workingCommand.TrimEnd().EndsWith("}"))
                {
                    workingCommand += ";";
                    ResetCompilationDiagnostics();
                    compiled = _evaluator.Compile(workingCommand);
                }

                if (compiled == null || _reportPrinter.HasErrors())
                {
                    List<UshellCompileDiagnostic> diagnostics = _reportPrinter.GetDiagnosticsSnapshot();
                    throw new UshellCompilationException(diagnostics);
                }

                object result = null;
                compiled(ref result);
                return new UshellEvaluationOutput
                {
                    ReturnValue = result,
                    Warnings = _reportPrinter.GetWarningMessages()
                };
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
                    _reportPrinter = new UshellCollectingReportPrinter();
                    _evaluator = new Evaluator(new CompilerContext(new CompilerSettings(), _reportPrinter));
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
                    _reportPrinter = null;
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

        private void ResetCompilationDiagnostics()
        {
            _reportPrinter?.ClearDiagnostics();
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
                UshellEvaluationOutput execution = Session.Evaluate(expression);
                stopwatch.Stop();

                UshellCodeExecutionResult result = new UshellCodeExecutionResult
                {
                    Success = true,
                    ReturnValue = execution.ReturnValue,
                    DurationMs = stopwatch.ElapsedMilliseconds
                };

                result.Warnings.AddRange(execution.Warnings);

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
            catch (UshellCompilationException exception)
            {
                stopwatch.Stop();
                UshellCodeExecutionResult failed = Failed("COMPILE_ERROR", exception.Message, exception.DetailedMessage);
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
