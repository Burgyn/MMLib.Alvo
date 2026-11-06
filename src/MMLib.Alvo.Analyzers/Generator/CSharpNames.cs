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

    private static readonly HashSet<string> _inheritedMembers = new(StringComparer.Ordinal)
    {
        "Record", "Patch", "Create", "Equals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize",
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
    /// The singular of an entity name by three rules only: <c>ies</c> → <c>y</c>; <c>(s|x|z|ch|sh)es</c> → drop <c>es</c>;
    /// a trailing <c>s</c> not after <c>s</c>, <c>u</c> or <c>i</c> dropped.
    /// </summary>
    internal static string Singular(string name)
    {
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

    /// <summary>The generated class of an entity: <c>Pascal(Singular(entity))</c>, a name the class's own members use suffixed with <c>Entity</c>.</summary>
    internal static string Class(string entity)
    {
        var name = Pascal(Singular(entity));
        name = name.Length == 0 ? "Entity" : name;

        return _reservedClassNames.Contains(name) ? name + "Entity" : name;
    }

    /// <summary>An enum member: <see cref="Pascal"/>, or <c>Value{index}</c> when nothing of the raw text is a letter or digit.</summary>
    internal static string Member(string raw, int index)
    {
        var name = Pascal(raw);

        return name.Length == 0 ? $"Value{index}" : name;
    }

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

    private static bool EndsWithSibilantPlural(string name) =>
        EndsWith(name, "ses") || EndsWith(name, "xes") || EndsWith(name, "zes") || EndsWith(name, "ches") || EndsWith(name, "shes");

    private static bool EndsWith(string name, string suffix) =>
        name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal);
}
