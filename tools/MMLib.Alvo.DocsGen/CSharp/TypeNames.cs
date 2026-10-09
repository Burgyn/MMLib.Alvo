using System.Reflection;

namespace MMLib.Alvo.DocsGen.CSharp;

internal static class TypeNames
{
    private static readonly Dictionary<Type, string> _keywords = new()
    {
        [typeof(string)] = "string",
        [typeof(object)] = "object",
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
    };

    internal static string Display(Type type, NullabilityInfo? nullability)
    {
        if (type.IsByRef)
        {
            return Display(type.GetElementType()!, nullability);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Display(underlying, null) + "?";
        }

        return Bare(type, nullability) + (IsNullableReference(type, nullability) ? "?" : string.Empty);
    }

    private static string Bare(Type type, NullabilityInfo? nullability)
    {
        if (type.IsArray)
        {
            return Display(type.GetElementType()!, nullability?.ElementType) + "[]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (_keywords.TryGetValue(type, out var keyword))
        {
            return keyword;
        }

        var name = type.IsGenericType ? Generic(type, nullability) : type.Name;
        return type.DeclaringType is { } outer ? $"{Bare(outer, null)}.{name}" : name;
    }

    private static string Generic(Type type, NullabilityInfo? nullability)
    {
        var name = Simple(type);
        var arguments = type.GetGenericArguments();
        var infos = nullability?.GenericTypeArguments;
        var rendered = arguments.Select((argument, index) => Display(argument, infos is { Length: > 0 } && index < infos.Length ? infos[index] : null));
        return $"{name}<{string.Join(", ", rendered)}>";
    }

    internal static string Simple(Type type)
    {
        var arity = type.Name.IndexOf('`', StringComparison.Ordinal);
        return arity >= 0 ? type.Name[..arity] : type.Name;
    }

    private static bool IsNullableReference(Type type, NullabilityInfo? nullability) =>
        !type.IsValueType && !type.IsGenericParameter && nullability?.ReadState == NullabilityState.Nullable;
}
