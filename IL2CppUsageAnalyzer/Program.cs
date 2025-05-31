using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NameNormalizer;

namespace IL2CppUsageAnalyzer;

public static class Program
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    [SuppressMessage("ReSharper", "CollectionNeverQueried.Global")]
    public class AnalyzedMethodInfo
    {
        public string ReturnType { get; set; } = string.Empty;
        public int MonoCount { get; set; }
        public int XrefCount { get; set; }
        public bool IsProperty { get; set; }
        public bool IsStripped { get; set; }
        public bool IsCompilerGenerated { get; set; }
        public bool IsGeneric { get; set; }
        public bool IsFlagged { get; set; }
        public List<string> MonoUsages { get; set; } = [];
        public List<string> XrefUsages { get; set; } = [];
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    public class XrefInfo
    {
        public int CallCount { get; set; }
        public string[] Usages { get; set; } = [];
    }

    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: IL2CppUsageAnalyzer <path to mono assembly csharp> <path to xref dump>");
            return;
        }
        var monoAssemblyPath = args[0];
        var xrefDump = args[1];

        if (!File.Exists(monoAssemblyPath) || !File.Exists(xrefDump))
        {
            Console.WriteLine("Invalid file paths provided.");
            return;
        }

        var methodInfos = AnalyzeMethods(monoAssemblyPath);
        var xrefMethodRefs = ParseXrefs(xrefDump);
        var xrefGenericCollapsed = xrefMethodRefs
            .Where(kvp => kvp.Key.Contains('<') && kvp.Key.Contains('>'))
            .GroupBy(kvp => Normalizer.CollapseGenerics(kvp.Key))
            .ToDictionary(
                g => g.Key,
                g => new XrefInfo {
                    CallCount = g.Sum(kvp => kvp.Value.CallCount),
                    Usages = g.SelectMany(kvp => kvp.Value.Usages).Distinct().ToArray()
                }
            );

        var strippedCount = 0;
        var inlinedCount = 0;
        var flaggedMethods = 0;

        foreach (var methodRef in methodInfos)
        {
            var xrefName = Normalizer.NormalizeXrefName(methodRef.Key);
            if (!xrefMethodRefs.TryGetValue(xrefName, out var xrefInfo) &&
                !xrefGenericCollapsed.TryGetValue(xrefName, out xrefInfo))
            {
                strippedCount++;
                methodRef.Value.IsStripped = true;
                continue;
            }

            methodRef.Value.XrefCount = xrefInfo.CallCount;
            methodRef.Value.XrefUsages.AddRange(xrefInfo.Usages);
            if (xrefInfo.CallCount != methodRef.Value.MonoCount)
            {
                inlinedCount++;
            }

            // compare mono and xref usages
            var monoUsagesSet = new HashSet<string>(methodRef.Value.MonoUsages.Select(Normalizer.NormalizeXrefName));
            var xrefUsagesSet = new HashSet<string>(xrefInfo.Usages);
            var missingInXref = monoUsagesSet.Except(xrefUsagesSet).ToList();
            var missingInMono = xrefUsagesSet.Except(monoUsagesSet).ToList();
            if (missingInXref.Count > 0 || missingInMono.Count > 0)
            {
                flaggedMethods++;
                methodRef.Value.IsFlagged = true;
            }
        }

        Console.WriteLine($"Total methods analyzed: {methodInfos.Count}");
        Console.WriteLine($"Methods stripped (no xref): {strippedCount}");
        Console.WriteLine($"Methods inlined (xref count != mono count): {inlinedCount}");
        Console.WriteLine($"Methods flagged (mismatched usages): {flaggedMethods}");
        Console.WriteLine();
        const string resultsFile = "method_analysis_results.json";
        var json = JsonSerializer.Serialize(methodInfos, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resultsFile, json);
        Console.WriteLine($"Analysis results saved to {resultsFile}");
    }

    private static Dictionary<string, XrefInfo> ParseXrefs(string xrefDumpPath)
    {
        var text = File.ReadAllText(xrefDumpPath);
        var xrefs = JsonSerializer.Deserialize<Dictionary<string, XrefInfo>>(text);
        if (xrefs == null)
        {
            throw new InvalidOperationException("Failed to parse xref dump.");
        }

        return xrefs;
    }

    private static Dictionary<string, AnalyzedMethodInfo> AnalyzeMethods(string assemblyPath)
    {
        var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var methodRefs = new Dictionary<string, AnalyzedMethodInfo>();
        var stateMachineToMethod = new Dictionary<string, string>();

        // First pass: index all methods
        foreach (var module in assembly.Modules)
        {
            foreach (var type in MonoUtils.GetAllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    var methodName = Normalizer.NormalizeMonoMethod(method);

                    if (method.CustomAttributes.Any(attr => attr.AttributeType.Name.Contains("IteratorStateMachineAttribute")))
                    {
                        var attr = method.CustomAttributes.First(a => a.AttributeType.Name.Contains("IteratorStateMachineAttribute"));
                        if (attr.ConstructorArguments.Count == 1)
                        {
                            if (attr.ConstructorArguments[0].Value is TypeReference stateMachineType)
                            {
                                stateMachineToMethod[stateMachineType.FullName] = methodName;
                            }
                        }
                    }

                    if (method.IsConstructor)
                    {
                        // Skip constructors
                        continue;
                    }

                    // look for attributes that indicate compiler-generated methods
                    var compilerGeneratedAttr = method.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name.Contains("CompilerGeneratedAttribute"));

                    // look for attributes that indicate compiler-generated type
                    var compilerGeneratedTypeAttr = type.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name.Contains("CompilerGeneratedAttribute"));

                    // Check if the method is generic
                    var isGeneric = method.HasGenericParameters;

                    var methodUsage = new AnalyzedMethodInfo
                    {
                        ReturnType = Normalizer.NormalizeMonoName(method.ReturnType.FullName),
                        IsProperty = method.IsSpecialName && (method.Name.Contains("get_") || method.Name.Contains("set_")),
                        IsCompilerGenerated = compilerGeneratedAttr != null || compilerGeneratedTypeAttr != null,
                        IsGeneric = isGeneric,
                    };
                    methodRefs.TryAdd(methodName, methodUsage);
                }
            }
        }

        // Second pass: find usages
        foreach (var module in assembly.Modules) {
            foreach (var type in MonoUtils.GetAllTypes(module)) {
                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    var methodName = Normalizer.NormalizeMonoMethod(method);
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        {
                            if (instr.Operand is MethodReference called)
                            {
                                var calledName = Normalizer.NormalizeMonoMethod(called);
                                if (!methodRefs.TryGetValue(calledName, out var methodUsage))
                                {
                                    continue;
                                }
                                methodUsage.MonoCount += 1;
                                methodUsage.MonoUsages.Add(Normalizer.NormalizeMonoMethod(method));
                            }
                        }

                        // Detect instantiations of state machine classes
                        if (instr.OpCode == OpCodes.Newobj && instr.Operand is MethodReference constructor)
                        {
                            var typeName = constructor.DeclaringType.FullName;
                            if (stateMachineToMethod.TryGetValue(typeName, out var coroutineMethod))
                            {
                                if (!methodRefs.TryGetValue(coroutineMethod, out var methodUsage))
                                {
                                    continue;
                                }

                                if (coroutineMethod == methodName)
                                {
                                    continue;
                                }

                                methodUsage.MonoCount += 1;
                                methodUsage.MonoUsages.Add(methodName);
                            }
                        }
                    }
                }
            }
        }
        return methodRefs;
    }
}