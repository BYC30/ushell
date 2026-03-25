using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

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

    public static class UshellRoslynExecutor
    {
        public static UshellCodeExecutionResult Execute(string expression, bool captureLogs, int timeoutMs)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource())
            {
                cancellationTokenSource.CancelAfter(Math.Max(timeoutMs, 100));
                UshellContext context = new UshellContext(cancellationTokenSource.Token);

                try
                {
                    Assembly codeAnalysisAssembly = TryLoadAssembly("Microsoft.CodeAnalysis");
                    Assembly csharpAssembly = TryLoadAssembly("Microsoft.CodeAnalysis.CSharp");
                    if (codeAnalysisAssembly == null || csharpAssembly == null)
                    {
                        return Failed("SERVICE_UNAVAILABLE", "Roslyn compiler assemblies could not be loaded from the current Unity Editor process.");
                    }

                    string source = WrapSource(expression);
                    Assembly compiledAssembly = Compile(codeAnalysisAssembly, csharpAssembly, source, out List<Dictionary<string, object>> diagnostics);
                    if (compiledAssembly == null)
                    {
                        return Failed("COMPILE_ERROR", "Editor expression failed to compile.", diagnostics);
                    }

                    Type entryType = compiledAssembly.GetType("Ushell.Dynamic.EntryPoint");
                    MethodInfo runMethod = entryType?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (runMethod == null)
                    {
                        return Failed("COMPILE_ERROR", "Compiled assembly did not contain the expected entry point.");
                    }

                    object taskObject = runMethod.Invoke(null, new object[] { context });
                    if (!(taskObject is Task task))
                    {
                        return Failed("COMPILE_ERROR", "Compiled entry point did not return a Task.");
                    }

                    if (!task.Wait(timeoutMs))
                    {
                        cancellationTokenSource.Cancel();
                        return Failed("EXEC_TIMEOUT", $"Execution exceeded the timeout of {timeoutMs}ms.");
                    }

                    if (task.IsFaulted)
                    {
                        Exception taskException = task.Exception;
                        if (taskException == null)
                        {
                            taskException = new InvalidOperationException("Unknown task failure.");
                        }

                        throw taskException;
                    }

                    object returnValue = task.GetType().GetProperty("Result")?.GetValue(task);
                    stopwatch.Stop();

                    UshellCodeExecutionResult result = new UshellCodeExecutionResult
                    {
                        Success = true,
                        ReturnValue = returnValue,
                        DurationMs = stopwatch.ElapsedMilliseconds
                    };

                    if (captureLogs)
                    {
                        result.Logs.AddRange(context.CapturedLogs);
                    }

                    return result;
                }
                catch (AggregateException aggregateException)
                {
                    return Failed("COMPILE_ERROR", aggregateException.Flatten().InnerException?.ToString() ?? aggregateException.ToString());
                }
                catch (TargetInvocationException invocationException)
                {
                    return Failed("COMPILE_ERROR", invocationException.InnerException?.ToString() ?? invocationException.ToString());
                }
                catch (OperationCanceledException)
                {
                    return Failed("EXEC_TIMEOUT", $"Execution exceeded the timeout of {timeoutMs}ms.");
                }
                catch (Exception exception)
                {
                    return Failed("COMPILE_ERROR", exception.ToString());
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

        private static string WrapSource(string expression)
        {
            string normalizedExpression = NormalizeExpression(expression);
            return $@"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Ushell.Editor;

namespace Ushell.Dynamic
{{
    public static class EntryPoint
    {{
        public static async Task<object> Run(UshellContext ctx)
        {{
            return {normalizedExpression};
        }}
    }}
}}";
        }

        private static string NormalizeExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException("Expression is required.");
            }

            string trimmed = expression.Trim();
            if (trimmed.EndsWith(";"))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            }

            return trimmed;
        }

        private static Assembly Compile(Assembly codeAnalysisAssembly, Assembly csharpAssembly, string source, out List<Dictionary<string, object>> diagnostics)
        {
            diagnostics = new List<Dictionary<string, object>>();

            Type metadataReferenceType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference");
            Type syntaxTreeType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.SyntaxTree");
            Type outputKindType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind");
            Type csharpSyntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            Type compilationType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            Type compilationOptionsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");

            MethodInfo createReferenceMethod = metadataReferenceType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "CreateFromFile" && method.GetParameters().Length >= 1);
            MethodInfo parseTextMethod = csharpSyntaxTreeType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "ParseText" && method.GetParameters().Length >= 1);
            MethodInfo compilationCreateMethod = compilationType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "Create" && method.GetParameters().Length >= 4);
            ConstructorInfo optionsConstructor = compilationOptionsType?.GetConstructors()
                .FirstOrDefault(constructor =>
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType == outputKindType;
                });

            if (createReferenceMethod == null || parseTextMethod == null || compilationCreateMethod == null || optionsConstructor == null)
            {
                return null;
            }

            object syntaxTree = BuildSyntaxTree(parseTextMethod, source);
            Array syntaxTrees = Array.CreateInstance(syntaxTreeType, 1);
            syntaxTrees.SetValue(syntaxTree, 0);

            List<object> references = new List<object>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location)))
            {
                try
                {
                    object reference = BuildReference(createReferenceMethod, assembly.Location);
                    if (reference != null)
                    {
                        references.Add(reference);
                    }
                }
                catch
                {
                    // Skip assemblies that cannot be referenced.
                }
            }

            Array referencesArray = Array.CreateInstance(metadataReferenceType, references.Count);
            for (int index = 0; index < references.Count; index++)
            {
                referencesArray.SetValue(references[index], index);
            }

            object outputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            object options = BuildCompilationOptions(optionsConstructor, outputKind);
            object compilation = compilationCreateMethod.Invoke(null, BuildCompilationArguments(compilationCreateMethod, $"Ushell_{Guid.NewGuid():N}", syntaxTrees, referencesArray, options));
            if (compilation == null)
            {
                return null;
            }

            using (MemoryStream memoryStream = new MemoryStream())
            {
                MethodInfo emitMethod = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "Emit" && method.GetParameters().Length >= 1 && method.GetParameters()[0].ParameterType == typeof(Stream));
                if (emitMethod == null)
                {
                    return null;
                }

                object[] emitArguments = BuildEmitArguments(emitMethod, memoryStream);
                object emitResult = emitMethod.Invoke(compilation, emitArguments);
                if (emitResult == null)
                {
                    return null;
                }

                bool success = (bool)emitResult.GetType().GetProperty("Success").GetValue(emitResult);
                IEnumerable diagnosticItems = (IEnumerable)emitResult.GetType().GetProperty("Diagnostics").GetValue(emitResult);
                foreach (object diagnostic in diagnosticItems)
                {
                    string severity = diagnostic.GetType().GetProperty("Severity")?.GetValue(diagnostic)?.ToString();
                    string message = diagnostic.GetType().GetMethod("GetMessage", Type.EmptyTypes)?.Invoke(diagnostic, null)?.ToString();
                    diagnostics.Add(new Dictionary<string, object>
                    {
                        { "severity", severity },
                        { "message", message }
                    });
                }

                if (!success)
                {
                    return null;
                }

                return Assembly.Load(memoryStream.ToArray());
            }
        }

        private static object BuildSyntaxTree(MethodInfo parseTextMethod, string source)
        {
            ParameterInfo[] parameters = parseTextMethod.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = source;
            FillDefaultArguments(parameters, arguments, 1);
            return parseTextMethod.Invoke(null, arguments);
        }

        private static object BuildReference(MethodInfo createReferenceMethod, string location)
        {
            ParameterInfo[] parameters = createReferenceMethod.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = location;
            FillDefaultArguments(parameters, arguments, 1);
            return createReferenceMethod.Invoke(null, arguments);
        }

        private static object BuildCompilationOptions(ConstructorInfo constructor, object outputKind)
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = outputKind;
            FillDefaultArguments(parameters, arguments, 1);
            return constructor.Invoke(arguments);
        }

        private static object[] BuildCompilationArguments(MethodInfo method, string assemblyName, Array syntaxTrees, Array references, object options)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = assemblyName;
            if (arguments.Length > 1)
            {
                arguments[1] = syntaxTrees;
            }

            if (arguments.Length > 2)
            {
                arguments[2] = references;
            }

            if (arguments.Length > 3)
            {
                arguments[3] = options;
            }

            FillDefaultArguments(parameters, arguments, 4);
            return arguments;
        }

        private static object[] BuildEmitArguments(MethodInfo emitMethod, MemoryStream memoryStream)
        {
            ParameterInfo[] parameters = emitMethod.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = memoryStream;
            for (int index = 1; index < arguments.Length; index++)
            {
                arguments[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : GetDefaultValue(parameters[index].ParameterType);
            }

            return arguments;
        }

        private static void FillDefaultArguments(ParameterInfo[] parameters, object[] arguments, int startIndex)
        {
            for (int index = startIndex; index < arguments.Length; index++)
            {
                arguments[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : GetDefaultValue(parameters[index].ParameterType);
            }
        }

        private static object GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static Assembly TryLoadAssembly(string name)
        {
            try
            {
                Assembly loadedAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (loadedAssembly != null)
                {
                    return loadedAssembly;
                }

                string[] probePaths = new[]
                {
                    Path.Combine(EditorApplication.applicationContentsPath, "Tools", "Roslyn", name + ".dll"),
                    Path.Combine(UshellPaths.ProjectPath, "Packages", "com.ushell", "Editor", "Plugins", "Roslyn", name + ".dll")
                };

                foreach (string probePath in probePaths)
                {
                    if (File.Exists(probePath))
                    {
                        return Assembly.LoadFrom(probePath);
                    }
                }

                return Assembly.Load(name);
            }
            catch
            {
                return null;
            }
        }
    }
}
