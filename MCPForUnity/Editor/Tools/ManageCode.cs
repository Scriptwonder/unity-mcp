using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Compiles and executes C# code snippets at runtime using Roslyn.
    /// Requires Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis DLLs in the project.
    /// </summary>
    [McpForUnityTool("execute_code", AutoRegister = false)]
    public static class ManageCode
    {
        private static Type s_syntaxTreeType;
        private static Type s_compilationType;
        private static Type s_compilationOptionsType;
        private static Type s_metadataReferenceType;
        private static bool s_roslynChecked;
        private static bool s_roslynAvailable;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            string code = p.Get("code");
            if (string.IsNullOrWhiteSpace(code))
                return new ErrorResponse("'code' parameter is required.");

            string entryType = p.Get("entryType") ?? "Validator";
            string entryMethod = p.Get("entryMethod") ?? "Run";
            int timeoutMs = p.GetInt("timeoutMs") ?? 5000;

            if (!CheckRoslynAvailable())
            {
                return new ErrorResponse(
                    "Roslyn (Microsoft.CodeAnalysis.CSharp) is not available. " +
                    "Install via the MCP for Unity window's Scripts tab (click 'Install Roslyn DLLs'). " +
                    "Or manually add Microsoft.CodeAnalysis.CSharp.dll and Microsoft.CodeAnalysis.dll " +
                    "to Assets/Plugins (from the Microsoft.CodeAnalysis.CSharp NuGet package).");
            }

            return CompileAndExecute(code, entryType, entryMethod, timeoutMs);
        }

        private static bool CheckRoslynAvailable()
        {
            if (s_roslynChecked) return s_roslynAvailable;
            s_roslynChecked = true;

            try
            {
                // Try to find Roslyn types in loaded assemblies
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    try
                    {
                        var name = asm.GetName().Name;
                        if (name == "Microsoft.CodeAnalysis.CSharp")
                        {
                            s_syntaxTreeType = asm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                            s_compilationType = asm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                            s_compilationOptionsType = asm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                        }
                        if (name == "Microsoft.CodeAnalysis")
                        {
                            s_metadataReferenceType = asm.GetType("Microsoft.CodeAnalysis.MetadataReference");
                        }
                    }
                    catch { }
                }

                s_roslynAvailable = s_syntaxTreeType != null && s_compilationType != null;
            }
            catch
            {
                s_roslynAvailable = false;
            }

            return s_roslynAvailable;
        }

        private static object CompileAndExecute(string code, string entryTypeName, string entryMethodName, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var logCapture = new List<string>();

            try
            {
                // Use Roslyn via reflection to avoid hard dependency
                // Parse the code
                var parseMethod = s_syntaxTreeType.GetMethod("ParseText",
                    new[] { typeof(string), s_syntaxTreeType.Assembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions"), typeof(string), System.Threading.CancellationToken.None.GetType() });

                // Simpler: use the overload that takes just a string
                var parseTextMethods = s_syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "ParseText")
                    .ToArray();

                object syntaxTree = null;
                foreach (var m in parseTextMethods)
                {
                    var methodParams = m.GetParameters();
                    if (methodParams.Length >= 1 && methodParams[0].ParameterType == typeof(string))
                    {
                        var args = new object[methodParams.Length];
                        args[0] = code;
                        for (int i = 1; i < args.Length; i++)
                        {
                            args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
                        }
                        syntaxTree = m.Invoke(null, args);
                        break;
                    }
                }

                if (syntaxTree == null)
                    return new ErrorResponse("Failed to parse code with Roslyn.");

                // Collect metadata references
                var createFromFileMethod = s_metadataReferenceType.GetMethod("CreateFromFile",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), s_metadataReferenceType.Assembly.GetType("Microsoft.CodeAnalysis.MetadataReferenceProperties") }, null);

                // Find the simpler overload
                var createFromFileMethods = s_metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "CreateFromFile")
                    .ToArray();

                MethodInfo createRefMethod = null;
                foreach (var m in createFromFileMethods)
                {
                    var methodParams = m.GetParameters();
                    if (methodParams.Length >= 1 && methodParams[0].ParameterType == typeof(string))
                    {
                        createRefMethod = m;
                        break;
                    }
                }

                if (createRefMethod == null)
                    return new ErrorResponse("Failed to find MetadataReference.CreateFromFile method.");

                var refsList = new List<object>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                    try
                    {
                        var refParams = createRefMethod.GetParameters();
                        var args = new object[refParams.Length];
                        args[0] = asm.Location;
                        for (int i = 1; i < args.Length; i++)
                            args[i] = refParams[i].HasDefaultValue ? refParams[i].DefaultValue : null;
                        refsList.Add(createRefMethod.Invoke(null, args));
                    }
                    catch { }
                }

                // Build a typed MetadataReference[] so it's assignable to IEnumerable<MetadataReference>
                var refs = Array.CreateInstance(s_metadataReferenceType, refsList.Count);
                for (int i = 0; i < refsList.Count; i++)
                    refs.SetValue(refsList[i], i);

                // Create compilation
                // OutputKind is in Microsoft.CodeAnalysis.dll (common), not the CSharp assembly
                var outputKindType = s_metadataReferenceType.Assembly.GetType("Microsoft.CodeAnalysis.OutputKind");
                var dllKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
                var optionsCtor = s_compilationOptionsType.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .First();
                var optionsCtorParams = optionsCtor.GetParameters();
                var optionsArgs = new object[optionsCtorParams.Length];
                optionsArgs[0] = dllKind; // outputKind is always first
                for (int i = 1; i < optionsArgs.Length; i++)
                    optionsArgs[i] = optionsCtorParams[i].HasDefaultValue ? optionsCtorParams[i].DefaultValue : null;
                var options = optionsCtor.Invoke(optionsArgs);

                // CSharpCompilation.Create(assemblyName, syntaxTrees, references, options)
                // SyntaxTree base type is in Microsoft.CodeAnalysis.dll (common), not the CSharp assembly
                var syntaxTreeBaseType = s_metadataReferenceType.Assembly.GetType("Microsoft.CodeAnalysis.SyntaxTree");
                var metaRefBaseType = s_metadataReferenceType;

                var createMethods = s_compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Create")
                    .ToArray();

                object compilation = null;
                foreach (var m in createMethods)
                {
                    var methodParams = m.GetParameters();
                    if (methodParams.Length >= 4 && methodParams[0].ParameterType == typeof(string))
                    {
                        // Build typed arrays via reflection
                        var syntaxTreeArrayType = syntaxTreeBaseType.MakeArrayType();
                        var treeArray = Array.CreateInstance(syntaxTreeBaseType, 1);
                        treeArray.SetValue(syntaxTree, 0);

                        var refArrayElementType = methodParams[2].ParameterType;
                        // It's IEnumerable<MetadataReference>, so just pass as a list
                        var createArgs = new object[methodParams.Length];
                        createArgs[0] = "MCPExecuteCode_" + Guid.NewGuid().ToString("N");
                        createArgs[1] = treeArray;
                        createArgs[2] = refs;
                        createArgs[3] = options;
                        for (int i = 4; i < createArgs.Length; i++)
                            createArgs[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;

                        try
                        {
                            compilation = m.Invoke(null, createArgs);
                            break;
                        }
                        catch { }
                    }
                }

                if (compilation == null)
                    return new ErrorResponse("Failed to create Roslyn compilation.");

                // Emit to memory stream
                using (var ms = new MemoryStream())
                {
                    var emitMethod = compilation.GetType().GetMethod("Emit",
                        new[] { typeof(Stream) });

                    // Find an Emit overload that takes a Stream
                    if (emitMethod == null)
                    {
                        var emitMethods = compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name == "Emit")
                            .ToArray();
                        foreach (var m in emitMethods)
                        {
                            var methodParams = m.GetParameters();
                            if (methodParams.Length >= 1 && methodParams[0].ParameterType == typeof(Stream))
                            {
                                emitMethod = m;
                                break;
                            }
                        }
                    }

                    if (emitMethod == null)
                        return new ErrorResponse("Failed to find Emit method on compilation.");

                    var emitArgs = new object[emitMethod.GetParameters().Length];
                    emitArgs[0] = ms;
                    for (int i = 1; i < emitArgs.Length; i++)
                    {
                        var ep = emitMethod.GetParameters()[i];
                        emitArgs[i] = ep.HasDefaultValue ? ep.DefaultValue : null;
                    }

                    var emitResult = emitMethod.Invoke(compilation, emitArgs);

                    // Check emitResult.Success
                    bool emitSuccess = (bool)emitResult.GetType().GetProperty("Success").GetValue(emitResult);
                    if (!emitSuccess)
                    {
                        var diagnostics = emitResult.GetType().GetProperty("Diagnostics").GetValue(emitResult);
                        var diagList = new List<string>();
                        foreach (var d in (System.Collections.IEnumerable)diagnostics)
                            diagList.Add(d.ToString());

                        sw.Stop();
                        return new SuccessResponse("Compilation failed.", new
                        {
                            success = false,
                            compile_errors = diagList,
                            output = (string)null,
                            return_value = (string)null,
                            duration_ms = sw.ElapsedMilliseconds,
                        });
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    var assemblyData = ms.ToArray();
                    var loadedAssembly = Assembly.Load(assemblyData);

                    // Find entry type and method
                    var type = loadedAssembly.GetType(entryTypeName);
                    if (type == null)
                    {
                        sw.Stop();
                        return new SuccessResponse("Compilation succeeded but entry type not found.", new
                        {
                            success = false,
                            compile_errors = new List<string>(),
                            output = $"Type '{entryTypeName}' not found in compiled assembly. Available types: {string.Join(", ", loadedAssembly.GetTypes().Select(t => t.FullName))}",
                            return_value = (string)null,
                            duration_ms = sw.ElapsedMilliseconds,
                        });
                    }

                    var method = type.GetMethod(entryMethodName, BindingFlags.Public | BindingFlags.Static);
                    if (method == null)
                    {
                        sw.Stop();
                        return new SuccessResponse("Compilation succeeded but entry method not found.", new
                        {
                            success = false,
                            compile_errors = new List<string>(),
                            output = $"Static method '{entryMethodName}' not found on type '{entryTypeName}'.",
                            return_value = (string)null,
                            duration_ms = sw.ElapsedMilliseconds,
                        });
                    }

                    // Capture Debug.Log output during execution
                    Application.LogCallback logHandler = (msg, stackTrace, logType) =>
                    {
                        logCapture.Add($"[{logType}] {msg}");
                    };
                    Application.logMessageReceived += logHandler;

                    object returnValue = null;
                    string executionError = null;
                    try
                    {
                        var methodParams = method.GetParameters();
                        object[] invokeArgs = null;
                        if (methodParams.Length > 0)
                        {
                            // If method expects a GameObject, pass null (user can find objects in code)
                            invokeArgs = new object[methodParams.Length];
                        }

                        returnValue = method.Invoke(null, invokeArgs);
                    }
                    catch (TargetInvocationException tie)
                    {
                        executionError = tie.InnerException?.Message ?? tie.Message;
                    }
                    catch (Exception ex)
                    {
                        executionError = ex.Message;
                    }
                    finally
                    {
                        Application.logMessageReceived -= logHandler;
                    }

                    sw.Stop();

                    string returnValueStr = null;
                    if (returnValue != null)
                    {
                        try
                        {
                            returnValueStr = Newtonsoft.Json.JsonConvert.SerializeObject(returnValue);
                        }
                        catch
                        {
                            returnValueStr = returnValue.ToString();
                        }
                    }

                    if (executionError != null)
                    {
                        return new SuccessResponse("Code compiled but execution failed.", new
                        {
                            success = false,
                            compile_errors = new List<string>(),
                            output = string.Join("\n", logCapture),
                            execution_error = executionError,
                            return_value = returnValueStr,
                            duration_ms = sw.ElapsedMilliseconds,
                        });
                    }

                    return new SuccessResponse("Code executed successfully.", new
                    {
                        success = true,
                        compile_errors = new List<string>(),
                        output = string.Join("\n", logCapture),
                        return_value = returnValueStr,
                        duration_ms = sw.ElapsedMilliseconds,
                    });
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                return new ErrorResponse($"Error in execute_code: {e.Message}");
            }
        }
    }
}
