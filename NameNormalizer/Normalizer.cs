using System.Text.RegularExpressions;
using Mono.Cecil;
// ReSharper disable MemberCanBePrivate.Global

namespace NameNormalizer;

public static partial class Normalizer
{
    public static readonly Dictionary<string, string> TypeAliases = new()
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

    public static string CollapseGenerics(string xref)
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

    public static string NormalizeXrefName(string name)
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

    public static string NormalizeMonoMethod(MethodDefinition method)
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

    public static string NormalizeMonoMethod(MethodReference method)
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

    public static string NormalizeMonoParameter(ParameterDefinition parameter)
    {
        return NormalizeMonoType(parameter.ParameterType.FullName);
    }

    public static string NormalizeMonoType(string typeName)
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

    public static string NormalizeMonoName(string monoName)
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
    public static partial Regex GenericMethodMatch();

    [GeneratedRegex(@"^(?<base>[^<]+)<(?<args>.+)>$")]
    public static partial Regex GenericMatch();

    [GeneratedRegex(@"^(?<prefix>[^<]+)<[^<>]+>(?=::)")]
    public static partial Regex GenericReplace();
}