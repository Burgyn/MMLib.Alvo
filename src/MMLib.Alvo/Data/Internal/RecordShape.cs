using MMLib.Alvo.Events;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// How one host type maps to an entity's fields, read once per type: each mapped property's field
/// (<see cref="AlvoFieldAttribute"/>, else snake_case), the value it holds, whether it takes null, its enum's values, the
/// constructor to call — and the problems no descriptor can cure.
/// </summary>
/// <remarks>
/// A property is mapped when it is public, readable and not an indexer, and has a public setter (<c>set</c> or <c>init</c>)
/// or carries <see cref="AlvoFieldAttribute"/>. A plain type is built through its parameterless constructor, else through
/// the public constructor with the most parameters that all name mapped properties and take their types (a positional record's); a wrapper
/// (<see cref="IAlvoEntity"/>) through its constructor taking one <see cref="AlvoRecord"/>.
/// </remarks>
internal sealed class RecordShape
{
    private static readonly ConcurrentDictionary<Type, RecordShape> _shapes = new();

    private readonly Dictionary<string, MappedProperty> _byName;

    private RecordShape(Type type)
    {
        Type = type;
        Entity = type.GetCustomAttribute<AlvoEntityAttribute>()?.Name;
        EventType = type.GetCustomAttribute<AlvoEventAttribute>()?.Type;
        IsWrapper = typeof(IAlvoEntity).IsAssignableFrom(type);
        var nullability = new NullabilityInfoContext();
        Properties = [.. Candidates(type).Select(property => MappedProperty.Of(property, nullability))];
        _byName = Properties.GroupBy(property => property.Property.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Constructor = IsWrapper ? type.GetConstructor([typeof(AlvoRecord)]) : Callable(type, Properties);
        Problems = ShapeProblems.Of(this);
    }

    internal Type Type { get; }

    internal string Name => Type.Name;

    internal string? Entity { get; }

    internal string? EventType { get; }

    internal bool IsWrapper { get; }

    internal IReadOnlyList<MappedProperty> Properties { get; }

    internal ConstructorInfo? Constructor { get; }

    internal IReadOnlyList<ShapeProblem> Problems { get; }

    internal static RecordShape Of(Type type) => _shapes.GetOrAdd(type, static shaped => new RecordShape(shaped));

    internal static MappedProperty? ParameterProperty(ParameterInfo parameter, IReadOnlyList<MappedProperty> properties) =>
        properties.FirstOrDefault(property =>
            string.Equals(property.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
            && parameter.ParameterType.IsAssignableFrom(property.Property.PropertyType));

    internal MappedProperty? Mapped(MemberInfo member) =>
        member is PropertyInfo property && _byName.TryGetValue(property.Name, out var mapped) ? mapped : null;

    private static IEnumerable<PropertyInfo> Candidates(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
            .Where(property => property.SetMethod is { IsPublic: true } || Attribute.IsDefined(property, typeof(AlvoFieldAttribute), inherit: true));

    private static ConstructorInfo? Callable(Type type, IReadOnlyList<MappedProperty> properties)
    {
        var constructors = type.GetConstructors();

        return constructors.FirstOrDefault(constructor => constructor.GetParameters().Length == 0)
            ?? constructors
                .Where(constructor => constructor.GetParameters().All(parameter => ParameterProperty(parameter, properties) is not null))
                .MaxBy(constructor => constructor.GetParameters().Length);
    }
}

/// <summary>One mapped property: its field, the value it holds (nullable unwrapped), whether it takes null, and its enum.</summary>
internal sealed record MappedProperty(PropertyInfo Property, string Field, Type ValueType, bool AcceptsNull, EnumMap? Enum)
{
    internal static MappedProperty Of(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var lifted = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = lifted ?? property.PropertyType;

        return new(property, FieldName(property), valueType, lifted is not null || TakesNull(property, nullability), valueType.IsEnum ? EnumMap.Of(valueType) : null);
    }

    private static string FieldName(PropertyInfo property) =>
        property.GetCustomAttribute<AlvoFieldAttribute>(inherit: true)?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);

    private static bool TakesNull(PropertyInfo property, NullabilityInfoContext nullability) =>
        !property.PropertyType.IsValueType && nullability.Create(property).WriteState != NullabilityState.NotNull;
}
