using HarmonyLib;
using Mono.Cecil;
using NameNormalizer;

namespace HarmonyPatchAnalyzer;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: HarmonyPatchAnalyzer <mod file path> <output file path>");
            return;
        }

        var modFilePath = args[0];
        var outputFilePath = args[1];

        if (!File.Exists(modFilePath))
        {
            Console.WriteLine("Invalid mod path provided.");
            return;
        }

        var methodNames = AnalyzeMod(modFilePath);
        using var writer = new StreamWriter(outputFilePath);
        foreach (var methodName in methodNames)
        {
            writer.WriteLine(methodName);
        }
    }

    private static string HarmonyPatchAttributeFullName { get; } = typeof(HarmonyPatch).FullName!;

    private static List<string> AnalyzeMod(string modFilePath)
    {
        var assembly = AssemblyDefinition.ReadAssembly(modFilePath);
        var methodNames = new List<string>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in MonoUtils.GetAllTypes(module))
            {
                string? typeRef = null;
                foreach (var attr in type.CustomAttributes.Where(x=>x.AttributeType.FullName == HarmonyPatchAttributeFullName))
                {
                    ScanAttribute(attr);
                    typeRef = attr.ConstructorArguments.Select(x => x.Value).OfType<TypeReference>().FirstOrDefault()?.FullName;
                }

                foreach (var method in type.Methods)
                {
                    foreach (var attr in method.CustomAttributes.Where(x=>x.AttributeType.FullName == HarmonyPatchAttributeFullName))
                    {
                        if (typeRef != null)
                        {
                            var methodRef = attr.ConstructorArguments.Select(x => x.Value).OfType<string>().FirstOrDefault();
                            if (methodRef != null)
                            {
                                methodNames.Add(Normalizer.NormalizeMonoName($"{typeRef}::{methodRef}"));
                            }
                        }
                        ScanAttribute(attr);
                    }
                }
            }
        }

        methodNames = methodNames.Distinct().OrderBy(x=>x).ToList();
        return methodNames;

        void ScanAttribute(CustomAttribute attribute)
        {
            var typeRef = attribute.ConstructorArguments.Select(x => x.Value).OfType<TypeReference>().FirstOrDefault();
            if (typeRef != null)
            {
                var methodRef = attribute.ConstructorArguments.Select(x => x.Value).OfType<string>().FirstOrDefault();
                if (methodRef != null)
                {
                    methodNames.Add(Normalizer.NormalizeMonoName($"{typeRef.FullName}::{methodRef}"));
                }
            }
        }
    }
}