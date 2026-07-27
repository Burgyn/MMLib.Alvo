using Microsoft.Extensions.FileSystemGlobbing;
using System.Xml.Linq;

namespace MMLib.Alvo.Conventions.Tests;

/// <summary>
/// os A — the EF dependency boundary, read off the project files rather than off loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// The family-wide runtime rule (<c>SharedArchitectureRules.No_public_surface_exposes_efs_context_or_change_tracker</c>)
/// matches EF's types by <em>name</em>, deliberately, so it keeps working in a project that cannot see EF at
/// all. The cost of that choice is that it says nothing about <b>who can see EF</b> — an assembly gaining an EF
/// reference it should never have had is invisible to it. This suite is the complement: it walks the
/// <c>ProjectReference</c>/<c>PackageReference</c> graph on disk and answers exactly that question.
/// </para>
/// <para>
/// The invariant that matters is about one project. <c>MMLib.Alvo.Testing</c> is referenced by <b>every</b>
/// test project (from <c>test/Directory.Build.props</c>, which is why this walker reads the props files too and
/// not only the <c>.csproj</c>), and it is the library that earns a package when *external provider authors*
/// need the contract suites. A dependency added there reaches every one of those authors — including one whose
/// store is not EF-backed — and every in-repo test project, including the ones that deliberately have no EF.
/// PR2 briefly did exactly that, to put the <c>IAlvoSqlDialect</c> contract suite somewhere; the suite now
/// lives in the companion <c>MMLib.Alvo.Testing.EntityFrameworkCore</c>, and these facts are what stop the
/// shortcut being taken again.
/// </para>
/// </remarks>
public class EfDependencyBoundaryTests
{
    private static readonly string _root = RepositoryRoot.Find();

    private static readonly IReadOnlyDictionary<string, ProjectNode> _projects = LoadProjects();

    private const string SharedTestSupport = "MMLib.Alvo.Testing";

    private const string RelationalTestSupport = "MMLib.Alvo.Testing.EntityFrameworkCore";

    /// <summary>
    /// The one that matters: the test-support library every test project inherits must not drag a
    /// relational infrastructure choice along with it.
    /// </summary>
    [Fact]
    public void The_shared_test_support_library_does_not_reach_ef_core()
    {
        var offenders = EfReferencesReachedBy(SharedTestSupport);

        offenders.ShouldBeEmpty(
            $"{SharedTestSupport} must stay Abstractions-only: it is referenced by every test project and "
            + "earns a package when external provider authors need the contract suites, so anything it "
            + "references, an author with a non-EF store inherits. A contract suite for a port that does not "
            + $"live in Abstractions belongs in {RelationalTestSupport}. Reached via: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The non-vacuity control. The fact above is a claim about a graph walk, and a walk that silently
    /// found nothing would satisfy it for the wrong reason — so the companion project, whose entire purpose
    /// is to hold the EF dependency, must be seen to reach it.
    /// </summary>
    [Fact]
    public void The_relational_test_support_library_does_reach_ef_core()
        => EfReferencesReachedBy(RelationalTestSupport).ShouldNotBeEmpty(
            $"{RelationalTestSupport} exists to carry the EF dependency the shared library must not. An empty "
            + "result here means the reference walk is broken, not that the boundary holds.");

    /// <summary>
    /// And the consequence, derived rather than listed: a test project that reaches no <c>MMLib.Alvo.Data.*</c>
    /// project has no business resolving EF Core either. Deriving it means a project added later is covered
    /// without anyone remembering to add it to a list.
    /// </summary>
    [Fact]
    public void A_test_project_with_no_data_package_reaches_no_ef_core()
    {
        var offenders = _projects.Values
            .Where(project => project.IsTestProject)
            .Where(project => !ClosureOf(project.Name).Any(IsDataPackage))
            .Where(project => EfReferencesReachedBy(project.Name).Count > 0)
            .Select(project => $"{project.Name} → {string.Join(", ", EfReferencesReachedBy(project.Name))}")
            .ToList();

        offenders.ShouldBeEmpty(
            "A test project that references none of the MMLib.Alvo.Data.* packages must not resolve EF Core "
            + $"transitively. Offenders: {string.Join("; ", offenders)}");
    }

    private static bool IsDataPackage(string name) =>
        name.StartsWith("MMLib.Alvo.Data.", StringComparison.Ordinal);

    private static bool IsEfPackage(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || name.StartsWith("Npgsql", StringComparison.Ordinal);

    /// <summary>
    /// Every EF/Npgsql package <paramref name="project"/> resolves, each reported as the project that
    /// declared it, so a failure names where to look rather than only what is wrong.
    /// </summary>
    private static IReadOnlyList<string> EfReferencesReachedBy(string project) =>
        [.. ClosureOf(project)
            .Where(_projects.ContainsKey)
            .SelectMany(reached => _projects[reached].Packages
                .Where(IsEfPackage)
                .Select(package => $"{reached} → {package}"))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// <paramref name="project"/> plus everything it references, transitively. Project names that are not
    /// in the solution (there are none today) stay in the result so a typo surfaces rather than vanishes.
    /// </summary>
    private static HashSet<string> ClosureOf(string project)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);

        while (pending.TryPop(out var current))
        {
            if (!reached.Add(current) || !_projects.TryGetValue(current, out var node))
            {
                continue;
            }

            foreach (var reference in node.Projects)
            {
                pending.Push(reference);
            }
        }

        return reached;
    }

    private static Dictionary<string, ProjectNode> LoadProjects()
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*.csproj");
        matcher.AddExclude("**/bin/**");
        matcher.AddExclude("**/obj/**");

        return matcher.GetResultsInFullPath(_root)
            .Select(Describe)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// One project's declared references — its own, plus every <c>Directory.Build.props</c> above it. The
    /// props are not an optional refinement: <c>test/Directory.Build.props</c> is the only place the
    /// <c>MMLib.Alvo.Testing</c> reference is declared for most test projects, so a walker reading only
    /// <c>.csproj</c> files would miss the exact edge these facts exist to police.
    /// </summary>
    private static ProjectNode Describe(string projectFilePath)
    {
        var directory = Path.GetDirectoryName(projectFilePath)!;
        var documents = new List<XDocument> { XDocument.Load(projectFilePath) };
        documents.AddRange(PropsAbove(directory).Select(XDocument.Load));

        var projects = documents
            .SelectMany(document => ReferencedNames(document, "ProjectReference"))
            .ToHashSet(StringComparer.Ordinal);
        var packages = documents
            .SelectMany(document => ReferencedNames(document, "PackageReference"))
            .ToHashSet(StringComparer.Ordinal);

        return new ProjectNode(
            Path.GetFileNameWithoutExtension(projectFilePath),
            Path.GetRelativePath(_root, projectFilePath).Replace('\\', '/').StartsWith("test/", StringComparison.Ordinal),
            projects,
            packages);
    }

    private static IEnumerable<string> PropsAbove(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                yield return candidate;
            }

            if (string.Equals(current, _root, StringComparison.Ordinal))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// The referenced <em>names</em> for one item type. A <c>ProjectReference</c> carries a path, so the
    /// name is its file name; a <c>PackageReference</c> carries the name directly.
    /// </summary>
    private static IEnumerable<string> ReferencedNames(XContainer document, string itemType) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == itemType)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => itemType == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(include!.Replace('\\', '/'))
                : include!);

    private sealed record ProjectNode(
        string Name, bool IsTestProject, IReadOnlySet<string> Projects, IReadOnlySet<string> Packages);
}
