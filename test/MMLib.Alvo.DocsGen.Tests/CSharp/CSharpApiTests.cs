using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Tests.CSharp;

public class CSharpApiTests
{
    private static readonly XmlDocs _docs = XmlDocs.Load(ShippedPackages.All.Select(p => p.Assembly));

    [Fact]
    public void The_package_list_is_every_packable_src_project()
    {
        var packable = Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !XDocument.Load(path).Descendants("IsPackable").Any(e => e.Value.Trim() == "false"))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);

        ShippedPackages.All.Select(p => p.Package).Order(StringComparer.Ordinal).ShouldBe(packable!);
    }

    [Fact]
    public void Every_in_scope_member_has_a_summary()
    {
        var missing = ShippedPackages.All
            .SelectMany(p => ApiScope.TypesOf(p.Assembly))
            .SelectMany(ApiScope.MembersOf)
            .Where(member => _docs.Summary(ApiScope.DocIdOf(member)).Length == 0)
            .Select(member => $"{member.DeclaringType!.Name}.{member.Name}")
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Every_in_scope_type_has_a_summary()
    {
        var missing = ShippedPackages.All
            .SelectMany(p => ApiScope.TypesOf(p.Assembly))
            .Where(type => _docs.Summary(DocId.Of(type)).Length == 0)
            .Select(type => type.Name)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Registration_entry_points_are_in_scope()
    {
        var types = ShippedPackages.All.SelectMany(p => ApiScope.TypesOf(p.Assembly)).Select(t => t.Name).ToList();
        types.ShouldContain("AlvoServiceCollectionExtensions");
        types.ShouldContain("AlvoBuilderExtensions");
        types.ShouldContain("AlvoEndpointRouteBuilderExtensions");
        types.ShouldContain("IAlvoData");
        types.ShouldContain("AlvoApiOptions");
    }

    [Fact]
    public void An_options_class_brings_the_classes_it_holds()
    {
        var types = ShippedPackages.All.SelectMany(p => ApiScope.TypesOf(p.Assembly)).Select(t => t.Name).ToList();

        types.ShouldContain("AlvoAuthOptions");
        types.ShouldContain("AlvoDevApiKey");
    }

    [Fact]
    public void Accessors_and_object_overrides_are_not_members()
    {
        var names = ShippedPackages.All
            .SelectMany(p => ApiScope.TypesOf(p.Assembly))
            .SelectMany(ApiScope.MembersOf)
            .Select(member => member.Name)
            .ToList();

        names.ShouldNotContain(name => name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal));
        names.ShouldNotContain(nameof(ToString));
        names.ShouldNotContain(nameof(GetHashCode));
    }

    [Fact]
    public void The_admin_surface_named_in_the_package_boundary_is_in_scope()
    {
        var text = Regex.Replace(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "docs", "architecture", "package-boundary.md")), @"\s+", " ");
        var start = text.IndexOf("The surface a host is meant to use is", StringComparison.Ordinal);
        var end = text.IndexOf("that register and map the dashboard", start, StringComparison.Ordinal);
        var named = Regex.Matches(text[start..end], "`([A-Za-z]+)`").Select(match => match.Groups[1].Value).ToList();
        var admin = ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo.Admin").Assembly;

        named.ShouldNotBeEmpty();
        named.ShouldBeSubsetOf(ApiScope.TypesOf(admin).Select(type => type.Name));
    }

    [Fact]
    public void Every_package_has_a_description()
    {
        foreach (var (package, assembly) in ShippedPackages.All)
        {
            var description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
            description.ShouldNotBeNullOrWhiteSpace(package);
            description.ShouldNotStartWith("Alvo — a .NET-native Backend-as-a-Service framework", Case.Sensitive, $"{package} still inherits the generic description");
        }
    }

    [Fact]
    public void The_core_page_shows_registration_with_signatures_and_summaries()
    {
        var (package, assembly) = ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo");

        var core = CSharpApiGenerator.RenderPackage(package, assembly, _docs, 12);

        core.RelativePath.ShouldBe("csharp/mmlib-alvo.md");
        core.Content.ShouldStartWith("---\ntitle: \"MMLib.Alvo\"\n");
        core.Content.ShouldContain("#### `AddAlvo`");
        core.Content.ShouldContain("#### `AddCelFunction`");
        core.Content.ShouldContain("#### `MapAlvo`");
        core.Content.ShouldContain(
            "```csharp\npublic static IAlvoBuilder AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null)\n```");
        core.Content.ShouldContain("- `name` — ");
        core.Content.ShouldContain("**Returns:** ");
    }

    [Fact]
    public void Remarks_render_as_a_paragraph_under_the_summary()
    {
        var (package, assembly) = ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo.Identity");

        var identity = CSharpApiGenerator.RenderPackage(package, assembly, _docs, 12).Content;

        var summary = identity.IndexOf("The DI key the cookie", StringComparison.Ordinal);
        var remarks = identity.IndexOf("**Keyed, and that is a security decision rather than a composition style.**", StringComparison.Ordinal);
        summary.ShouldBeGreaterThan(0);
        remarks.ShouldBeGreaterThan(summary);
        identity[summary..remarks].ShouldContain("\n\n");
    }

    [Fact]
    public void The_identity_constants_are_in_scope()
    {
        var identity = ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo.Identity").Assembly;

        ApiScope.TypesOf(identity).Select(type => type.Name).ShouldContain("AlvoIdentity");
    }

    [Fact]
    public void A_package_without_host_facing_types_says_so()
    {
        var page = CSharpApiGenerator.RenderPackage("Fixture", "A fixture package.", [], XmlDocs.Parse("<doc><members/></doc>"), 20);

        page.Content.ShouldContain(CSharpApiGenerator.NoHostFacingTypes);
        page.Content.ShouldNotContain("## Namespace");
    }

    [Fact]
    public void The_index_lists_every_package()
    {
        var index = CSharpApiGenerator.RenderIndex(ShippedPackages.All);

        index.RelativePath.ShouldBe("csharp/index.md");
        index.Content.ShouldStartWith("---\ntitle: \"C# API\"\n");
        foreach (var (package, _) in ShippedPackages.All)
        {
            index.Content.ShouldContain($"(/MMLib.Alvo/reference/csharp/{package.ToLowerInvariant().Replace('.', '-')}/)");
        }
    }
}
