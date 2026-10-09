using MMLib.Alvo.Admin;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data.PostgreSql;
using MMLib.Alvo.Data.Sqlite;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using MMLib.Alvo.Identity;
using MMLib.Alvo.Migrations;
using System.Reflection;

namespace MMLib.Alvo.DocsGen.Configuration;

internal sealed record ConfigurationKey(string Key, string Type, string Default, string Description);

internal sealed record ConfigurationSection(string Name, string Scope, Type OptionsType, IReadOnlyList<ConfigurationKey> Keys);

internal static class OptionsCatalog
{
    internal const string HostScope = "Standalone host (the `mmlib/alvo` image)";
    internal const string HostBoundScope = "Bound by the standalone host; an embedded host configures it in code";
    internal const string CoreScope = "Any host (bound by the core)";

    private const string OptionsSuffix = "Options";
    private static readonly string[] _sectionConstants = ["ConfigurationSection", "SectionName"];

    internal static IReadOnlyDictionary<Type, string> SectionsWithoutAConstant { get; } = new Dictionary<Type, string>
    {
        [typeof(AlvoAuthOptions)] = "Alvo:Auth",
        [typeof(AlvoApiOptions)] = "Alvo:Api",
    };

    internal static IReadOnlySet<Type> BoundOnlyByTheStandaloneHost { get; } = new HashSet<Type>(SectionsWithoutAConstant.Keys)
    {
        typeof(AlvoIdentityOptions),
        typeof(AlvoAdminOptions),
    };

    internal static IReadOnlyDictionary<Type, string> NotBoundFromConfiguration { get; } = new Dictionary<Type, string>
    {
        [typeof(AlvoOptions)] = "Configured in code with `Configure<AlvoOptions>`; no host binds it from configuration.",
        [typeof(SqliteProviderOptions)] = "Set in code by `UseSqlite(…)`; its connection string comes from `ConnectionStrings:Alvo`, listed below.",
        [typeof(PostgreSqlProviderOptions)] = "Set in code by `UsePostgreSql(…)`; its connection string comes from `ConnectionStrings:Alvo`, listed below.",
        [typeof(MigrationOptions)] = "A per-call argument to the schema migrator, not a configuration section; the `Alvo:Schema` keys decide what start-up passes it.",
    };

    internal static IReadOnlyList<ConfigurationSection> Read(IEnumerable<Assembly> assemblies, XmlDocs docs)
    {
        var candidates = Discover(assemblies);
        var nested = candidates.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Select(property => property.PropertyType).ToHashSet();
        return [.. candidates
            .Where(type => !nested.Contains(type) && !NotBoundFromConfiguration.ContainsKey(type))
            .Select(type => Section(type, docs))
            .OrderBy(section => section.Name, StringComparer.Ordinal)];
    }

    internal static List<Type> Discover(IEnumerable<Assembly> assemblies) =>
    [
        .. assemblies.Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsClass && !type.IsAbstract && type.Name.EndsWith(OptionsSuffix, StringComparison.Ordinal)),
    ];

    private static ConfigurationSection Section(Type type, XmlDocs docs)
    {
        var name = SectionName(type);
        return new ConfigurationSection(name, ScopeOf(type), type, OptionsKeys.Walk(name, type, docs));
    }

    private static string ScopeOf(Type type) =>
        type.Assembly == ShippedAssemblies.Host ? HostScope
        : BoundOnlyByTheStandaloneHost.Contains(type) ? HostBoundScope
        : CoreScope;

    private static string SectionName(Type type) =>
        SectionConstant(type)
        ?? (Companion(type) is { } companion ? SectionConstant(companion) : null)
        ?? SectionsWithoutAConstant.GetValueOrDefault(type)
        ?? throw new InvalidOperationException(
            $"{type.FullName} is a public options type with no configuration section. Give it (or {type.Name[..^OptionsSuffix.Length]}) a public const "
            + "ConfigurationSection, or list it in OptionsCatalog.SectionsWithoutAConstant or NotBoundFromConfiguration.");

    private static string? SectionConstant(Type type) =>
        _sectionConstants
            .Select(name => type.GetField(name, BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            ?.GetRawConstantValue() as string;

    private static Type? Companion(Type type) =>
        type.Assembly.GetExportedTypes().FirstOrDefault(candidate => candidate.Name == type.Name[..^OptionsSuffix.Length]);
}
