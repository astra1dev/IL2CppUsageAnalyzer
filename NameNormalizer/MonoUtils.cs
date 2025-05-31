using Mono.Cecil;

namespace NameNormalizer;

public static class MonoUtils
{
    public static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in GetNestedTypesRecursive(type))
                yield return nested;
        }
    }

    public static IEnumerable<TypeDefinition> GetNestedTypesRecursive(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var subNested in GetNestedTypesRecursive(nested))
                yield return subNested;
        }
    }
}