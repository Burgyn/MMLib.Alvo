using System.Globalization;
using System.Reflection;

namespace MMLib.Alvo.DocsGen.Xml;

internal static class DocId
{
    internal static string Of(Type type) => "T:" + TypeName(type);

    internal static string Of(FieldInfo field) => $"F:{TypeName(field.DeclaringType!)}.{field.Name}";

    internal static string Of(PropertyInfo property)
    {
        var indexers = property.GetIndexParameters();
        var parameters = indexers.Length == 0 ? string.Empty : $"({string.Join(',', indexers.Select(p => ParameterType(p.ParameterType)))})";
        return $"P:{TypeName(property.DeclaringType!)}.{property.Name}{parameters}";
    }

    internal static string Of(MethodBase method)
    {
        var name = method is ConstructorInfo ? "#ctor" : method.Name;
        var arity = method.IsGenericMethod ? "``" + method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var parameters = method.GetParameters();
        var list = parameters.Length == 0 ? string.Empty : $"({string.Join(',', parameters.Select(p => ParameterType(p.ParameterType)))})";
        return $"M:{TypeName(method.DeclaringType!)}.{name}{arity}{list}";
    }

    private static string TypeName(Type type) => (type.FullName ?? $"{type.Namespace}.{type.Name}").Replace('+', '.');

    private static string ParameterType(Type type)
    {
        if (type.IsByRef)
        {
            return ParameterType(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            return ParameterType(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericMethodParameter)
        {
            return "``" + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        }

        if (type.IsGenericTypeParameter)
        {
            return "`" + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        }

        return type.IsGenericType ? GenericInstance(type) : TypeName(type);
    }

    private static string GenericInstance(Type type)
    {
        var definition = TypeName(type.GetGenericTypeDefinition());
        var bare = definition[..definition.IndexOf('`', StringComparison.Ordinal)];
        return $"{bare}{{{string.Join(',', type.GetGenericArguments().Select(ParameterType))}}}";
    }
}
