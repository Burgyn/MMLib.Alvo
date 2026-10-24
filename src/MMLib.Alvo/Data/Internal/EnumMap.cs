using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Data.Internal;

/// <summary>One enum type's members and the declared values they stand for: <see cref="AlvoEnumValueAttribute"/>, else snake_case.</summary>
internal sealed class EnumMap
{
    private static readonly ConcurrentDictionary<Type, EnumMap> _maps = new();

    private readonly Dictionary<string, object> _byValue;

    private EnumMap(Type type)
    {
        Type = type;
        Members = [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(Member)];
        _byValue = Members.GroupBy(member => member.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Member, StringComparer.Ordinal);
    }

    internal Type Type { get; }

    internal IReadOnlyList<EnumMember> Members { get; }

    internal bool IsFlags => Type.IsDefined(typeof(FlagsAttribute), inherit: false);

    internal static EnumMap Of(Type type) => _maps.GetOrAdd(type, static enumType => new EnumMap(enumType));

    internal string? ValueOf(object member) => Members.FirstOrDefault(entry => entry.Member.Equals(member))?.Value;

    internal object? MemberOf(string value) => _byValue.GetValueOrDefault(value);

    private static EnumMember Member(FieldInfo field) => new(
        field.Name,
        field.GetCustomAttribute<AlvoEnumValueAttribute>()?.Value ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name),
        field.GetValue(null)!);
}

/// <summary>One enum member, by name, the value it stands for, and itself.</summary>
internal sealed record EnumMember(string Name, string Value, object Member);
