using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// The C# names generated from descriptor names: PascalCase types and members, camelCase parameters, singular class names
/// — each a compiling identifier whatever the raw text, the raw text kept in the attributes that map it.
/// </summary>
internal static class CSharpNames
{
    private static readonly HashSet<string> _reservedClassNames = new(StringComparer.Ordinal) { "Record", "Patch", "Create" };

    private static readonly HashSet<string> _implicitlyImported = new(StringComparer.Ordinal)
    {
        "Action", "Array", "Attribute", "Boolean", "Byte", "CancellationToken", "Char", "Console", "Convert", "DateOnly", "DateTime",
        "DateTimeOffset", "Decimal", "Delegate", "Dictionary", "Directory", "Double", "Enum", "Environment", "EventArgs", "Exception",
        "File", "Func", "Guid", "HashSet", "Host", "HttpClient", "HttpContext", "Index", "Int16", "Int32", "Int64", "Lazy", "List",
        "Lock", "LogLevel", "Math", "Monitor", "Nullable", "Object", "Parallel", "Path", "Random", "Range", "Results", "Single",
        "Stream", "String", "Task", "TaskStatus", "Thread", "TimeOnly", "TimeSpan", "Timer", "Tuple", "Type", "Uri", "ValueTask", "Version",
    };

    private static readonly KeyValuePair<string, string>[] _irregular =
    [
        new("statuses", "status"),
        new("buses", "bus"),
        new("people", "person"),
        new("children", "child"),
        new("data", "data"),
    ];

    private static readonly HashSet<string> _inheritedMembers = new(StringComparer.Ordinal)
    {
        "Record", "Patch", "Create", "Equals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize", "ReferenceEquals",
    };

    private static readonly HashSet<string> _objectMembers = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize", "ReferenceEquals",
    };

    private static readonly HashSet<string> _enumMembers = new(_objectMembers, StringComparer.Ordinal)
    {
        "CompareTo", "Format", "GetName", "GetNames", "GetTypeCode", "GetUnderlyingType", "GetValues", "GetValuesAsUnderlyingType",
        "HasFlag", "IsDefined", "Parse", "TryFormat", "TryParse",
    };

    /// <summary>
    /// <paramref name="raw"/> split on every character that is not a letter or digit, each part capitalised and joined; a
    /// leading digit gets <c>_</c>; an empty result is the empty string.
    /// </summary>
    internal static string Pascal(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var startOfPart = true;
        foreach (var c in raw)
        {
            if (!char.IsLetterOrDigit(c))
            {
                startOfPart = true;
                continue;
            }

            builder.Append(startOfPart ? char.ToUpperInvariant(c) : c);
            startOfPart = false;
        }

        return builder.Length > 0 && char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    /// <summary>A parameter name: <see cref="Pascal"/> with its first letter lowered, <c>@</c>-escaped when it is a reserved keyword.</summary>
    internal static string Camel(string raw)
    {
        var pascal = Pascal(raw);
        var camel = pascal.Length == 0 ? "value" : char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);

        return SyntaxFacts.GetKeywordKind(camel) == SyntaxKind.None ? camel : "@" + camel;
    }

    /// <summary>
    /// The singular of an entity name: a last word in the irregular table (<c>statuses</c>, <c>buses</c>, <c>people</c>,
    /// <c>children</c>, <c>data</c>) is replaced; otherwise <c>ies</c> → <c>y</c>; <c>(sses|xes|zes|ches|shes)</c> → drop
    /// <c>es</c>; else a trailing <c>s</c> is dropped unless the name ends in <c>ss</c>, <c>us</c> or <c>is</c>.
    /// </summary>
    internal static string Singular(string name)
    {
        if (Irregular(name) is { } irregular)
        {
            return irregular;
        }

        if (EndsWith(name, "ies"))
        {
            return name.Substring(0, name.Length - 3) + "y";
        }

        if (EndsWithSibilantPlural(name))
        {
            return name.Substring(0, name.Length - 2);
        }

        return name.Length > 1 && name[name.Length - 1] == 's' && name[name.Length - 2] is not ('s' or 'u' or 'i')
            ? name.Substring(0, name.Length - 1)
            : name;
    }

    /// <summary>
    /// The type names the SDK's implicit global usings (and the web SDK's) bring into every host file: a generated type of that
    /// name would shadow the BCL type inside its namespace and make it ambiguous wherever the namespace is imported.
    /// </summary>
    internal static IReadOnlyCollection<string> ImplicitlyImportedTypeNames => _implicitlyImported;

    /// <summary>
    /// The generated class of an entity: <c>Pascal(Singular(entity))</c>, suffixed with <c>Entity</c> when the class's own
    /// members use the name or an implicitly imported BCL type already has it.
    /// </summary>
    internal static string Class(string entity)
    {
        var name = Pascal(Singular(entity));
        name = name.Length == 0 ? "Entity" : name;

        return _reservedClassNames.Contains(name) || _implicitlyImported.Contains(name) ? name + "Entity" : name;
    }

    /// <summary>
    /// The enum of a field: <c>{Class}{Property}</c>, suffixed with <c>Enum</c> when a type the SDK's implicit global usings
    /// bring in already has that name.
    /// </summary>
    internal static string EnumType(string className, string property)
    {
        var name = className + property;

        return _implicitlyImported.Contains(name) ? name + "Enum" : name;
    }

    /// <summary>An enum member: <see cref="Pascal"/>, or <c>Value{index}</c> when nothing of the raw text is a letter or digit.</summary>
    internal static string Member(string raw, int index)
    {
        var name = Pascal(raw);

        return name.Length == 0 ? $"Value{index}" : name;
    }

    /// <summary>An enum member: <see cref="Member"/>, suffixed with <c>Value</c> when <see cref="System.Enum"/> or <see cref="object"/> already has a member of that name.</summary>
    internal static string EnumMember(string raw, int index) => Suffixed(Member(raw, index), _enumMembers);

    /// <summary>A role constant: <see cref="Member"/>, suffixed with <c>Value</c> when <see cref="object"/> already has a member of that name.</summary>
    internal static string RoleMember(string raw, int index) => Suffixed(Member(raw, index), _objectMembers);

    /// <summary>A property: <see cref="Pascal"/>, suffixed with <c>Value</c> when it is the class's name or a member the class already has.</summary>
    internal static string Property(string field, string className)
    {
        var name = Pascal(field);
        name = name.Length == 0 ? "Field" : name;

        return string.Equals(name, className, StringComparison.Ordinal) || _inheritedMembers.Contains(name) ? name + "Value" : name;
    }

    /// <summary><paramref name="name"/>, or — when <paramref name="used"/> already holds it — the first of <c>name2</c>, <c>name3</c>, … it does not; the answer is added.</summary>
    internal static string Unique(string name, HashSet<string> used)
    {
        var unique = name;
        for (var suffix = 2; !used.Add(unique); suffix++)
        {
            unique = name + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return unique;
    }

    /// <summary>The roles class of a project: <c>Pascal(project) + "Roles"</c>.</summary>
    internal static string Roles(string project) => Pascal(project) + "Roles";

    /// <summary>The patch class of an entity's class.</summary>
    internal static string Patch(string className) => className + "Patch";

    /// <summary>Every type name emitting <paramref name="entity"/> declares: its class, its patch and its enums.</summary>
    internal static IEnumerable<string> TypeNames(EntityModel entity) =>
        new[] { entity.ClassName, Patch(entity.ClassName) }
            .Concat(entity.Fields.Where(field => field.EnumTypeName is not null).Select(field => field.EnumTypeName!));

    private static string Suffixed(string name, HashSet<string> reserved) => reserved.Contains(name) ? name + "Value" : name;

    private static string? Irregular(string name)
    {
        foreach (var pair in _irregular)
        {
            if (EndsWithWord(name, pair.Key))
            {
                return name.Substring(0, name.Length - pair.Key.Length) + pair.Value;
            }
        }

        return null;
    }

    private static bool EndsWithWord(string name, string word) =>
        name.EndsWith(word, StringComparison.Ordinal)
        && (name.Length == word.Length || !char.IsLetterOrDigit(name[name.Length - word.Length - 1]));

    private static bool EndsWithSibilantPlural(string name) =>
        EndsWith(name, "sses") || EndsWith(name, "xes") || EndsWith(name, "zes") || EndsWith(name, "ches") || EndsWith(name, "shes");

    private static bool EndsWith(string name, string suffix) =>
        name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal);
}
