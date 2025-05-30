using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IL2CppUsageAnalyzer;

public static partial class Program
{
    [JsonSerializable(typeof(AnalyzedMethodInfo))]
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

    [JsonSerializable(typeof(Dictionary<string, AnalyzedMethodInfo>))]
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
            .GroupBy(kvp => CollapseGenerics(kvp.Key))
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
            if (!xrefMethodRefs.TryGetValue(NormalizeXrefName(methodRef.Key), out var xrefInfo) &&
                !xrefGenericCollapsed.TryGetValue(NormalizeXrefName(methodRef.Key), out xrefInfo))
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
            var monoUsagesSet = new HashSet<string>(methodRef.Value.MonoUsages.Select(NormalizeXrefName));
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

    private static readonly Dictionary<string, string> TypeAliases = new()
    {
        ["System.Void"] = "void",
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "unsignedchar",
        ["System.SByte"] = "signedchar",
        ["System.Char"] = "wchar_t",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "unsignedshort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "unsignedint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "unsignedlong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
    };

    private static Dictionary<string, AnalyzedMethodInfo> AnalyzeMethods(string assemblyPath)
    {
        var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var methodRefs = new Dictionary<string, AnalyzedMethodInfo>();
        var stateMachineToMethod = new Dictionary<string, string>();

        // First pass: index all methods
        foreach (var module in assembly.Modules)
        {
            foreach (var type in GetAllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    var methodName = NormalizeMonoMethod(method);

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
                        ReturnType = NormalizeMonoName(method.ReturnType.FullName),
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
            foreach (var type in GetAllTypes(module)) {
                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    var methodName = NormalizeMonoMethod(method);
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        {
                            if (instr.Operand is MethodReference called)
                            {
                                var calledName = NormalizeMonoMethod(called);
                                if (!methodRefs.TryGetValue(calledName, out var methodUsage))
                                {
                                    continue;
                                }
                                methodUsage.MonoCount += 1;
                                methodUsage.MonoUsages.Add(NormalizeMonoMethod(method));
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

    private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in GetNestedTypesRecursive(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> GetNestedTypesRecursive(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var subNested in GetNestedTypesRecursive(nested))
                yield return subNested;
        }
    }

    private static string CollapseGenerics(string xref)
    {
        var match = GenericMethodMatch().Match(xref);

        if (!match.Success)
        {
            return GenericReplace().Replace(xref, "${prefix}");
        }

        var prefix = match.Groups["prefix"].Value;
        var genericType = match.Groups["generic"].Value;
        var parameters = match.Groups["params"].Value;

        var collapsedParams = parameters.Replace(genericType, "T");

        var result = $"{prefix}{collapsedParams}";
        return result;
    }

    private static string NormalizeXrefName(string name)
    {
        name = name.Replace("<>", "__");
        if (name.Contains(">d"))
        {
            var parts = name.Split(">d");
            var firstColon = parts[1].IndexOf("::", StringComparison.Ordinal);
            var before = parts[1].Substring(0, firstColon);
            var after = parts[1].Substring(firstColon + 2);
            name = $"{parts[0]}_d{before}::{after.Replace("::", "_")}";
        }

        name = name.Replace(">d__", "_d__").Replace(">b__", "_b__").Replace("::<", "::_");
        return name;
    }

    private static string NormalizeMonoMethod(MethodDefinition method)
    {
        var typeName = NormalizeMonoName(method.DeclaringType.FullName);
        var methodName = NormalizeMonoName(method.Name);
        if (method.Parameters.Count == 0)
        {
            return $"{typeName}::{methodName}(void)";
        }
        var parameters = string.Join(",", method.Parameters.Select(NormalizeMonoParameter));
        return $"{typeName}::{methodName}({parameters})";
    }

    private static string NormalizeMonoMethod(MethodReference method)
    {
        var typeName = NormalizeMonoName(method.DeclaringType.FullName);
        var methodName = NormalizeMonoName(method.Name);
        if (method.Parameters.Count == 0)
        {
            return $"{typeName}::{methodName}(void)";
        }
        var parameters = string.Join(",", method.Parameters.Select(NormalizeMonoParameter));
        return $"{typeName}::{methodName}({parameters})";
    }

    private static string NormalizeMonoParameter(ParameterDefinition parameter)
    {
        return NormalizeMonoType(parameter.ParameterType.FullName);
    }

    private static string NormalizeMonoType(string typeName)
    {
        // Shortcut if the entire type matches an alias
        if (TypeAliases.TryGetValue(typeName, out var alias))
            return alias;

        // Handle generics like System::Func<T,System::Single>
        // Match base type and inner generic arguments
        var match = GenericMatch().Match(typeName);
        if (match.Success)
        {
            var baseType = match.Groups["base"].Value;
            var args = match.Groups["args"].Value;

            // Split by comma, normalize each, and rejoin
            var normalizedArgs = args
                .Split(',')
                .Select(arg => NormalizeMonoType(arg.Trim()))
                .ToArray();

            return NormalizeMonoName($"{baseType}<{string.Join(",", normalizedArgs)}>");
        }

        // Not a generic type, return normalized name
        return NormalizeMonoName(typeName);
    }

    private static string NormalizeMonoName(string monoName)
    {
        if (string.IsNullOrEmpty(monoName))
            return monoName;

        return monoName
            .Replace("<.cctor>", "__cctor_")    // replace <.cctor> with __cctor_
            .Replace("<.ctor>", "__ctor_")      // replace <.ctor> with __ctor_
            .Replace("TEnum", "T")              // replace TEnum with T
            .Replace("\u00601", "")             // replace `1 with empty string
            .Replace("\u00602", "")             // replace `2 with empty string
            .Replace("\u0060", "")              // replace ` with empty string
            .Replace(".", "::")                 // replace . with ::
            .Replace("/", "::");                // replace / with ::
    }

    [GeneratedRegex(@"^(?<prefix>.+?)<(?<generic>[^<>]+)>(?<params>\(.*\))$")]
    private static partial Regex GenericMethodMatch();

    [GeneratedRegex(@"^(?<base>[^<]+)<(?<args>.+)>$")]
    private static partial Regex GenericMatch();

    [GeneratedRegex(@"^(?<prefix>[^<]+)<[^<>]+>(?=::)")]
    private static partial Regex GenericReplace();
}