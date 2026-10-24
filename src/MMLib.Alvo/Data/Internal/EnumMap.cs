using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Data.Internal;

/// <summary>One enum type's members and the declared values they stand for: <see cref="AlvoEnumValueAttribute"/>, else snake_case.</summary>
/// <remarks>
/// Members are looked up as boxed enum values: a caller holding the underlying integral (an expression tree's
/// <c>Convert</c> to <c>Int32</c>, say) passes it through <see cref="Enum.ToObject(Type, object)"/> first, since a
/// boxed <c>1</c> is not a boxed member.
/// </remarks>
internal sealed class EnumMap
{
    private static readonly ConcurrentDictionary<Type, EnumMap> _maps = new();

    private readonly Dictionary<string, object> _byValue;

    private readonly Dictionary<object, string> _byMember;

    private EnumMap(Type type)
    {
        Type = type;
        IsFlags = type.IsDefined(typeof(FlagsAttribute), inherit: false);
        Members = [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(Member)];
        _byValue = Members.GroupBy(member => member.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Member, StringComparer.Ordinal);
        _byMember = Members.GroupBy(member => member.Member)
            .ToDictionary(group => group.Key, group => group.First().Value);
    }

    internal Type Type { get; }

    internal IReadOnlyList<EnumMember> Members { get; }

    internal bool IsFlags { get; }

    internal static EnumMap Of(Type type) => _maps.GetOrAdd(type, static enumType => new EnumMap(enumType));

    internal string? ValueOf(object member) => _byMember.GetValueOrDefault(member);

    internal object? MemberOf(string value) => _byValue.GetValueOrDefault(value);

    /// <summary>
    /// The member of <paramref name="enumType"/> a raw value stands for: an enum value by its number, an integral only when
    /// it fits the enum's underlying type; otherwise <see langword="null"/> and why.
    /// </summary>
    internal static Enum? MemberFor(Type enumType, object raw, out string reason)
    {
        reason = string.Empty;
        if (raw is Enum)
        {
            return (Enum)Enum.ToObject(enumType, raw);
        }

        if (raw is not (sbyte or byte or short or ushort or int or uint or long or ulong))
        {
            reason = $"a {raw.GetType().Name} is not a member of {enumType.Name}";
            return null;
        }

        var underlying = Enum.GetUnderlyingType(enumType);
        try
        {
            return (Enum)Enum.ToObject(enumType, Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            reason = $"{raw} is outside the range of {enumType.Name}'s underlying {underlying.Name}";
            return null;
        }
    }

    private static EnumMember Member(FieldInfo field) => new(
        field.Name,
        field.GetCustomAttribute<AlvoEnumValueAttribute>()?.Value ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name),
        field.GetValue(null)!);
}

/// <summary>One enum member, by name, the value it stands for, and itself.</summary>
internal sealed record EnumMember(string Name, string Value, object Member);
