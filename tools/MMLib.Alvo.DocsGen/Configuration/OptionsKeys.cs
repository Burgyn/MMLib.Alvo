using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using System.Globalization;
using System.Reflection;

namespace MMLib.Alvo.DocsGen.Configuration;

internal static class OptionsKeys
{
    internal const string NoDefault = "—";
    internal const string EmptyCollection = "empty";

    private static readonly Dictionary<Type, string> _keywords = new()
    {
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(int)] = "int",
        [typeof(long)] = "long",
        [typeof(short)] = "short",
        [typeof(byte)] = "byte",
        [typeof(double)] = "double",
        [typeof(float)] = "float",
        [typeof(decimal)] = "decimal",
    };

    private static readonly HashSet<Type> _structuredScalars = [typeof(TimeSpan), typeof(Guid), typeof(DateTimeOffset), typeof(DateTime), typeof(Uri)];

    private static readonly NullabilityInfoContext _nullability = new();

    internal static IReadOnlyList<ConfigurationKey> Walk(string section, Type type, XmlDocs docs)
    {
        var keys = new List<ConfigurationKey>();
        WalkObject(keys, section, type, Create(type), docs);
        return keys;
    }

    internal static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum || _keywords.ContainsKey(underlying) || _structuredScalars.Contains(underlying);
    }

    internal static string TypeLabel(Type type, bool nullableReference)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var bare = underlying ?? type;
        var name = bare.IsEnum ? "one of: " + string.Join(", ", Enum.GetNames(bare)) : _keywords.GetValueOrDefault(bare, bare.Name);
        return underlying is not null || nullableReference ? (bare.IsEnum ? name + " (or none)" : name + "?") : name;
    }

    internal static string DefaultOf(object? value) => value switch
    {
        null => NoDefault,
        string { Length: 0 } => "`\"\"`",
        string text => Md.Code(text),
        bool flag => flag ? "true" : "false",
        Enum member => member.ToString(),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? NoDefault,
    };

    private static void WalkObject(List<ConfigurationKey> keys, string prefix, Type type, object? instance, XmlDocs docs)
    {
        foreach (var property in ConfigurableProperties(type))
        {
            AddProperty(keys, $"{prefix}:{property.Name}", property, instance is null ? null : property.GetValue(instance), docs);
        }
    }

    private static IEnumerable<PropertyInfo> ConfigurableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Where(property => property.GetSetMethod() is not null || !IsScalar(property.PropertyType));

    private static void AddProperty(List<ConfigurationKey> keys, string key, PropertyInfo property, object? value, XmlDocs docs)
    {
        var type = property.PropertyType;
        var description = docs.Summary(DocId.Of(property));
        if (IsScalar(type))
        {
            keys.Add(new ConfigurationKey(key, TypeLabel(type, IsNullableReference(property)), DefaultOf(value), description));
        }
        else if (DictionaryValueType(type) is { } valueType)
        {
            AddElement(keys, key + ":{name}", valueType, description, docs);
        }
        else if (ElementType(type) is { } elementType)
        {
            AddElement(keys, key + ":{n}", elementType, description, docs);
        }
        else
        {
            WalkObject(keys, key, type, value, docs);
        }
    }

    private static void AddElement(List<ConfigurationKey> keys, string key, Type elementType, string description, XmlDocs docs)
    {
        if (IsScalar(elementType))
        {
            keys.Add(new ConfigurationKey(key, TypeLabel(elementType, nullableReference: false), EmptyCollection, description));
            return;
        }

        WalkObject(keys, key, elementType, Create(elementType), docs);
    }

    private static bool IsNullableReference(PropertyInfo property) =>
        !property.PropertyType.IsValueType && _nullability.Create(property).ReadState == NullabilityState.Nullable;

    private static Type? DictionaryValueType(Type type) =>
        EnumerableInterfaces(type)
            .FirstOrDefault(candidate => candidate.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                && candidate.GetGenericArguments()[0] == typeof(string))
            ?.GetGenericArguments()[1];

    private static Type? ElementType(Type type) =>
        type.IsArray
            ? type.GetElementType()
            : EnumerableInterfaces(type).FirstOrDefault(candidate => candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static IEnumerable<Type> EnumerableInterfaces(Type type) =>
        (type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces()).Where(candidate => candidate.IsGenericType);

    private static object? Create(Type type) =>
        type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
}
