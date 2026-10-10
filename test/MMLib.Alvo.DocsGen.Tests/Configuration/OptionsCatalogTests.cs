using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.DocsGen.Configuration;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using MMLib.Alvo.Host;
using System.Reflection;
using ConfigurationSection = MMLib.Alvo.DocsGen.Configuration.ConfigurationSection;

namespace MMLib.Alvo.DocsGen.Tests.Configuration;

public class OptionsCatalogTests
{
    private static readonly IReadOnlyList<Assembly> _assemblies = [.. ShippedAssemblies.All, ShippedAssemblies.Host];
    private static readonly IReadOnlyList<ConfigurationSection> _sections = OptionsCatalog.Read(_assemblies, XmlDocs.Load(_assemblies));

    [Fact]
    public void Every_public_options_type_is_resolved_or_explicitly_unbound()
    {
        _sections.Select(s => s.Name).ShouldContain("Alvo:Api");
        _sections.Select(s => s.Name).ShouldContain("Alvo:Management");
        _sections.Single(s => s.Name == "Alvo").Keys.Select(k => k.Key).ShouldContain("Alvo:Database:Provider");
    }

    [Fact]
    public void The_sections_are_exactly_what_discovery_finds() =>
        _sections.Select(s => s.Name).ShouldBe(
        [
            "Alvo", "Alvo:Admin", "Alvo:Admin:Dashboard", "Alvo:Ai", "Alvo:Api", "Alvo:Auth", "Alvo:Events", "Alvo:Management",
            "Alvo:Schema", "Alvo:Secrets",
        ]);

    [Fact]
    public void Every_discovered_type_is_a_section_a_nested_section_or_unbound()
    {
        var accounted = _sections.Select(s => s.OptionsType).Concat(OptionsCatalog.NotBoundFromConfiguration.Keys).ToHashSet();

        OptionsCatalog.Discover(_assemblies).Where(type => !accounted.Contains(type))
            .ShouldBe([typeof(AlvoHostForwardedHeadersOptions), typeof(AlvoHostDatabaseOptions), typeof(AlvoHostDocsOptions)], ignoreOrder: true);
    }

    [Fact]
    public void A_collection_of_objects_is_indexed()
    {
        var auth = _sections.Single(s => s.Name == "Alvo:Auth");

        auth.Keys.Select(k => k.Key).ShouldContain("Alvo:Auth:DevKeys:{n}:KeyId");
        auth.Keys.Select(k => k.Key).ShouldContain("Alvo:Auth:DevKeys:{n}:Roles:{n}");
        auth.Keys.Single(k => k.Key == "Alvo:Auth:HeaderName").Default.ShouldBe("`X-Alvo-Api-Key`");
    }

    [Fact]
    public void Types_defaults_and_descriptions_come_from_the_code()
    {
        var events = _sections.Single(s => s.Name == "Alvo:Events").Keys;
        var poll = events.Single(k => k.Key == "Alvo:Events:PollInterval");

        poll.Type.ShouldBe("TimeSpan");
        poll.Default.ShouldBe("00:00:01");
        poll.Description.ShouldNotBeEmpty();
        events.Single(k => k.Key == "Alvo:Events:WebhookAllowedNetworks:{n}").Default.ShouldBe(OptionsKeys.EmptyCollection);
        _sections.Single(s => s.Name == "Alvo:Secrets").Keys.Select(k => k.Key).ShouldContain("Alvo:Secrets:Values:{name}");
        _sections.Single(s => s.Name == "Alvo:Ai").Keys.Single(k => k.Key == "Alvo:Ai:Model").Type.ShouldBe("string?");
        _sections.Single(s => s.Name == "Alvo:Schema").Keys.Single(k => k.Key == "Alvo:Schema:Startup").Type.ShouldStartWith("one of: ");
    }

    [Fact]
    public void Each_section_says_who_binds_it()
    {
        _sections.Single(s => s.Name == "Alvo").Scope.ShouldBe(OptionsCatalog.HostScope);
        _sections.Single(s => s.Name == "Alvo:Api").Scope.ShouldBe(OptionsCatalog.HostBoundScope);
        _sections.Single(s => s.Name == "Alvo:Admin").Scope.ShouldBe(OptionsCatalog.HostBoundScope);
        _sections.Single(s => s.Name == "Alvo:Events").Scope.ShouldBe(OptionsCatalog.CoreScope);
    }

    [Fact]
    public void The_keys_the_standalone_host_overwrites_say_so()
    {
        var dashboard = _sections.Single(s => s.Name == "Alvo:Admin:Dashboard").Keys;
        var host = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Host", "AlvoHost.cs"));

        dashboard.Single(k => k.Key == "Alvo:Admin:Dashboard:DocsPath").Description.ShouldContain($"`{AlvoHost.ScalarPath}`");
        dashboard.Single(k => k.Key == "Alvo:Admin:Dashboard:OpenApiPath").Description.ShouldContain($"`{AlvoHost.OpenApiDocumentPath}`");
        dashboard.Single(k => k.Key == "Alvo:Admin:Dashboard:Enabled").Description.ShouldNotContain("overwrites");
        host.ShouldContain("admin.DocsPath = options.Docs.Enabled ? ScalarPath : null;");
        host.ShouldContain("admin.OpenApiPath = options.Docs.Enabled ? OpenApiDocumentPath : null;");
        OptionsCatalog.OverwrittenByTheStandaloneHost.Keys.ShouldAllBe(key => _sections.SelectMany(s => s.Keys).Any(k => k.Key == key));
    }

    [Fact]
    public void A_scalar_collection_default_is_read_from_the_instance()
    {
        OptionsKeys.CollectionDefault(new List<string>()).ShouldBe(OptionsKeys.EmptyCollection);
        OptionsKeys.CollectionDefault(new List<string> { "10.0.0.0/8", "fd00::/8" }).ShouldBe("`10.0.0.0/8`, `fd00::/8`");
        OptionsKeys.CollectionDefault(new Dictionary<string, string> { ["smtp"] = "x" }).ShouldBe("smtp = `x`");
        OptionsKeys.CollectionDefault(null).ShouldBe(OptionsKeys.NoDefault);
    }

    [Theory]
    [InlineData("Alvo:Api:DefaultPageSize", "7")]
    [InlineData("Alvo:Auth:HeaderName", "X-Docs-Probe-Key")]
    public async Task The_sections_without_a_constant_are_the_ones_the_host_binds(string key, string value)
    {
        var builder = AlvoHost.CreateBuilder([], configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alvo:DescriptorPath"] = Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"),
            ["Alvo:Database:Provider"] = "sqlite",
            ["Alvo:Database:SqliteConnectionString"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"docsgen-{Guid.NewGuid():N}.db")}",
            [key] = value,
        }));
        await using var app = builder.Build();

        BoundValue(app.Services, key).ShouldBe(value);
    }

    [Fact]
    public void A_section_row_renders_exactly() =>
        ConfigurationGenerator.Row("Alvo:Api:MaxPageSize", "int", "200", "The largest page | ever.")
            .ShouldBe("| `Alvo:Api:MaxPageSize` | int | 200 | The largest page \\| ever. |\n");

    private static string BoundValue(IServiceProvider services, string key) => key switch
    {
        _ when key.StartsWith("Alvo:Api:", StringComparison.Ordinal) =>
            services.GetRequiredService<IOptions<AlvoApiOptions>>().Value.DefaultPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => services.GetRequiredService<IOptions<AlvoAuthOptions>>().Value.HeaderName,
    };
}
