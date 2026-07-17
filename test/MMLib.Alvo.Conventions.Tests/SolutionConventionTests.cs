using Microsoft.Extensions.FileSystemGlobbing;
using System.Xml.Linq;

namespace MMLib.Alvo.Conventions.Tests;

/// <summary>
/// os A — solution-structure conventions enforced by scanning project and
/// solution files on disk, never loading an assembly or forcing a build. These
/// are the mechanical rules the <c>alvo-new-package</c> skill documents; the
/// skill says what to do, this test verifies every project actually did it.
/// Every project is parsed once into a <see cref="ProjectDescriptor"/>; the
/// facts each rule needs are read from there.
/// </summary>
public class SolutionConventionTests
{
    private static readonly string _root = RepositoryRoot.Find();

    private static readonly string[] _inheritedProperties =
        ["TargetFramework", "TargetFrameworks", "Nullable", "ImplicitUsings", "LangVersion"];

    private static readonly IReadOnlyList<ProjectDescriptor> _projects = LoadProjects();

    private static readonly HashSet<string> _registeredProjectPaths = LoadRegisteredProjectPaths();

    [Fact]
    public void No_project_pins_an_inline_package_version()
    {
        var offenders = _projects
            .Where(project => project.PinsPackageVersion || project.DeclaresAssemblyVersion)
            .Select(project => project.RelativePath)
            .ToList();

        offenders.ShouldBeEmpty(
            "Package versions belong in Directory.Packages.props (CPM); the assembly version is owned by MinVer.");
    }

    [Fact]
    public void No_project_redeclares_an_inherited_msbuild_property()
    {
        var offenders = _projects
            .Where(project => project.RedeclaredInheritedProperties.Count > 0)
            .Select(project => $"{project.RelativePath} ({string.Join(", ", project.RedeclaredInheritedProperties)})")
            .ToList();

        offenders.ShouldBeEmpty(
            "These properties are inherited from Directory.Build.props — do not re-declare them per project.");
    }

    [Fact]
    public void Every_project_is_registered_in_the_solution()
    {
        var missing = _projects
            .Where(project => !_registeredProjectPaths.Contains(project.RelativePath))
            .Select(project => project.RelativePath)
            .ToList();

        missing.ShouldBeEmpty(
            "Every project must be registered in MMLib.Alvo.slnx — run: dotnet sln MMLib.Alvo.slnx add <csproj>.");
    }

    [Fact]
    public void All_projects_follow_the_family_naming()
    {
        var offenders = _projects
            .Where(project => !project.Name.StartsWith("MMLib.Alvo.", StringComparison.Ordinal))
            .Select(project => project.RelativePath)
            .ToList();

        offenders.ShouldBeEmpty("All projects must be named MMLib.Alvo.*.");
    }

    [Fact]
    public void Src_projects_do_not_reference_test_projects()
    {
        var offenders = _projects
            .Where(project => project.TopLevelFolder == "src")
            .SelectMany(project => project.TestProjectReferences
                .Select(reference => $"{project.RelativePath} → {reference}"))
            .ToList();

        offenders.ShouldBeEmpty("src projects must not reference test projects.");
    }

    [Fact]
    public void Every_packable_src_project_has_a_tests_project()
    {
        var testProjectNames = _projects
            .Select(project => project.Name)
            .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missing = _projects
            .Where(project => project.TopLevelFolder == "src" && project.IsPackable)
            .Where(project => !testProjectNames.Contains($"{project.Name}.Tests"))
            .Select(project => project.Name)
            .ToList();

        missing.ShouldBeEmpty(
            "Every packable src project needs a matching test project — create test/<name>.Tests, "
            + "or mark the project <IsPackable>false</IsPackable> if it is internal.");
    }

    private static List<ProjectDescriptor> LoadProjects()
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*.csproj");
        matcher.AddExclude("**/bin/**");
        matcher.AddExclude("**/obj/**");

        return matcher.GetResultsInFullPath(_root)
            .Select(Describe)
            .OrderBy(project => project.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> LoadRegisteredProjectPaths() =>
        ElementsNamed(XDocument.Load(Path.Combine(_root, "MMLib.Alvo.slnx")), "Project")
            .Select(project => project.Attribute("Path")?.Value?.Replace('\\', '/'))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static ProjectDescriptor Describe(string projectFilePath)
    {
        var document = XDocument.Load(projectFilePath);
        var relativePath = Path.GetRelativePath(_root, projectFilePath).Replace('\\', '/');

        var pinsPackageVersion = ElementsNamed(document, "PackageReference")
            .Any(reference => reference.Attribute("Version") is not null
                || reference.Attribute("VersionOverride") is not null);

        var declaresAssemblyVersion = ElementsNamed(document, "Version")
            .Any(version => version.Parent?.Name.LocalName == "PropertyGroup");

        var redeclaredInheritedProperties = _inheritedProperties
            .Where(property => ElementsNamed(document, property).Any())
            .ToList();

        var testProjectReferences = ElementsNamed(document, "ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value?.Replace('\\', '/'))
            .Where(include => include is not null && include.Contains("/test/", StringComparison.OrdinalIgnoreCase))
            .Select(include => include!)
            .ToList();

        return new ProjectDescriptor(
            relativePath,
            Path.GetFileNameWithoutExtension(projectFilePath),
            relativePath.Split('/')[0],
            IsPackable(document),
            pinsPackageVersion,
            declaresAssemblyVersion,
            redeclaredInheritedProperties,
            testProjectReferences);
    }

    private static bool IsPackable(XDocument project) =>
        !string.Equals(
            ElementsNamed(project, "IsPackable").FirstOrDefault()?.Value.Trim(),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<XElement> ElementsNamed(XContainer document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);

    private sealed record ProjectDescriptor(
        string RelativePath,
        string Name,
        string TopLevelFolder,
        bool IsPackable,
        bool PinsPackageVersion,
        bool DeclaresAssemblyVersion,
        IReadOnlyList<string> RedeclaredInheritedProperties,
        IReadOnlyList<string> TestProjectReferences);
}
