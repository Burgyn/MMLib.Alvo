using MMLib.Alvo.Testing;
using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace MMLib.Alvo.Conventions.Tests;

/// <summary>
/// os A — solution-structure conventions enforced by scanning project and
/// solution files on disk, never loading an assembly or forcing a build. These
/// are the mechanical rules the <c>alvo-new-package</c> skill documents; the
/// skill says what to do, this test verifies every project actually did it.
/// </summary>
public class SolutionConventionTests
{
    private static readonly string _root = RepositoryRoot.Find();

    private static IReadOnlyList<string> ProjectFiles() =>
        Directory.EnumerateFiles(_root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsInFolder(path, "bin") && !IsInFolder(path, "obj"))
            .ToList();

    private static bool IsInFolder(string path, string folder) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    private static bool IsPackable(XDocument project) =>
        !string.Equals(
            project.Descendants("IsPackable").FirstOrDefault()?.Value.Trim(),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private static string RelativePath(string absolutePath) =>
        Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');

    [Fact]
    public void No_project_pins_an_inline_package_version()
    {
        var offenders = new List<string>();
        foreach (var projectFile in ProjectFiles())
        {
            var document = XDocument.Load(projectFile);
            if (document.Descendants("PackageReference").Any(reference => reference.Attribute("Version") is not null))
            {
                offenders.Add($"{RelativePath(projectFile)} (PackageReference/@Version)");
            }

            if (document.Descendants("Version").Any(version => version.Parent?.Name.LocalName == "PropertyGroup"))
            {
                offenders.Add($"{RelativePath(projectFile)} (<Version>)");
            }
        }

        offenders.ShouldBeEmpty(
            "Package versions belong in Directory.Packages.props (CPM); the assembly version is owned by MinVer.");
    }

    [Fact]
    public void No_project_redeclares_an_inherited_msbuild_property()
    {
        string[] inherited = ["TargetFramework", "TargetFrameworks", "Nullable", "ImplicitUsings", "LangVersion"];
        var offenders = new List<string>();
        foreach (var projectFile in ProjectFiles())
        {
            var document = XDocument.Load(projectFile);
            foreach (var property in inherited)
            {
                if (document.Descendants(property).Any())
                {
                    offenders.Add($"{RelativePath(projectFile)} (<{property}>)");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "These properties are inherited from Directory.Build.props — do not re-declare them per project.");
    }

    [Fact]
    public void Every_project_is_registered_in_the_solution()
    {
        var solution = XDocument.Load(Path.Combine(_root, "MMLib.Alvo.slnx"));
        var registered = solution.Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value)
            .Where(path => path is not null)
            .Select(path => Path.GetFileName(path!.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = ProjectFiles()
            .Select(Path.GetFileName)
            .Where(name => !registered.Contains(name!))
            .ToList();

        missing.ShouldBeEmpty("Every project must be registered in MMLib.Alvo.slnx.");
    }

    [Fact]
    public void All_projects_follow_the_family_naming()
    {
        var offenders = ProjectFiles()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.StartsWith("MMLib.Alvo.", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty("All projects must be named MMLib.Alvo.*.");
    }

    [Fact]
    public void Src_projects_do_not_reference_test_projects()
    {
        var offenders = new List<string>();
        foreach (var projectFile in ProjectFiles().Where(path => IsInFolder(path, "src")))
        {
            var document = XDocument.Load(projectFile);
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value?.Replace('\\', '/');
                if (include is not null && include.Contains("/test/", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{RelativePath(projectFile)} → {include}");
                }
            }
        }

        offenders.ShouldBeEmpty("src projects must not reference test projects.");
    }

    [Fact]
    public void Every_packable_src_project_has_a_tests_project()
    {
        var testProjectNames = ProjectFiles()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.EndsWith(".Tests", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var projectFile in ProjectFiles().Where(path => IsInFolder(path, "src")))
        {
            if (!IsPackable(XDocument.Load(projectFile)))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(projectFile);
            if (!testProjectNames.Contains($"{name}.Tests"))
            {
                missing.Add(name!);
            }
        }

        missing.ShouldBeEmpty("Every packable src project must have a matching *.Tests project.");
    }
}
