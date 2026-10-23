using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The package's boundary as <c>docs/architecture/package-boundary.md</c> states it: three public types, one provider,
/// no internals granted to or by it, and the four references it declares.
/// </summary>
/// <remarks>
/// The provider and <c>InternalsVisibleTo</c> facts read the loaded assemblies, because an assembly names what was
/// linked. The reference set is read off the project file instead: <c>Microsoft.Extensions.TimeProvider.Testing</c>
/// is carried for the package's consumers and no TestHost code uses it, so the compiler drops it from the assembly's
/// references and only the project file still names it.
/// </remarks>
public sealed class TestHostBoundaryTests
{
    private const string AssemblyName = "MMLib.Alvo.TestHost";

    private static readonly Assembly _testHost = typeof(AlvoTestHost).Assembly;

    [Fact]
    public void The_public_surface_is_the_three_approved_types() =>
        _testHost.GetExportedTypes().Select(type => type.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["AlvoDrainNotQuiescentException", "AlvoHookRecorder", "AlvoTestHost"]);

    [Fact]
    public void It_takes_the_sqlite_provider_and_no_other() =>
        _testHost.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("MMLib.Alvo.Data.", StringComparison.Ordinal))
            .ShouldBe(["MMLib.Alvo.Data.Sqlite"]);

    [Fact]
    public void It_grants_no_internals() =>
        _testHost.GetCustomAttributes<InternalsVisibleToAttribute>().ShouldBeEmpty();

    [Fact]
    public void No_assembly_it_references_grants_it_internals()
    {
        var family = _testHost.GetReferencedAssemblies()
            .Where(reference => reference.Name!.StartsWith("MMLib.Alvo", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .ToList();

        family.ShouldNotBeEmpty();
        family
            .Where(assembly => assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
                .Any(grant => grant.AssemblyName == AssemblyName))
            .Select(assembly => assembly.GetName().Name)
            .ShouldBeEmpty();
    }

    [Fact]
    public void It_declares_the_four_references_and_nothing_else()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot.Find(), "src", AssemblyName, $"{AssemblyName}.csproj"));

        Included(project, "ProjectReference").Select(Path.GetFileNameWithoutExtension)
            .ShouldBe(["MMLib.Alvo", "MMLib.Alvo.Data.Sqlite"], ignoreOrder: true);
        Included(project, "PackageReference").ShouldBe(["Microsoft.Extensions.TimeProvider.Testing"]);
        Included(project, "FrameworkReference").ShouldBe(["Microsoft.AspNetCore.App"]);
    }

    private static IEnumerable<string> Included(XDocument project, string item) =>
        project.Descendants(item).Select(element => (string)element.Attribute("Include")!);
}
