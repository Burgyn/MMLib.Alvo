using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Ai.Tests;
using System.Reflection;
using System.Xml.Linq;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The package boundary (package-boundary.md rules (a) and (b)) and the core's InternalsVisibleTo grant (S2 D1).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it may reference:</b> Abstractions, the core, and the IdentityModel packages that earn it — nothing else
/// outside the shared framework. A data driver here would make sign-in an engine's feature; the dashboard or identity
/// here would hand every host that wants end-user sign-in an operator console too.
/// </para>
/// <para>
/// <b>What the grant is for:</b> the core's sign-in seams. It reaches every other core internal as well, so the
/// shared guard the Ai, Admin and Identity suites hold their grants with holds this one: no allow decision, no
/// compiled expression, no application role, and none of the ambient flow's setters.
/// </para>
/// </remarks>
public sealed class AccountsArchitectureTests
{
    private const string IdentityModelPrefix = "Microsoft.IdentityModel.";

    private static readonly Assembly _accounts = typeof(AlvoAccounts).Assembly;

    private static readonly string[] _alvoReferences = ["MMLib.Alvo.Abstractions", "MMLib.Alvo"];

    private static readonly string[] _sharedFrameworkDirectories =
    [
        Path.GetDirectoryName(typeof(object).Assembly.Location)!,
        Path.GetDirectoryName(typeof(HttpContext).Assembly.Location)!,
    ];

    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(_accounts);

    /// <summary>The control: the assembly as compiled references both Alvo assemblies it is allowed.</summary>
    [Fact]
    public void It_references_the_abstractions_and_the_core()
        => _alvoReferences.ShouldBeSubsetOf(_accounts.GetReferencedAssemblies().Select(reference => reference.Name!));

    /// <summary>
    /// Beyond the shared framework it adds IdentityModel and nothing else to what the core already brings (the core's
    /// own OpenAPI packages arrive with it, and their source generator compiles into every assembly that references it).
    /// </summary>
    [Fact]
    public void Beyond_the_shared_framework_it_adds_only_IdentityModel_to_what_the_core_brings()
    {
        var core = Assembly.Load("MMLib.Alvo");
        string[] admitted = [.. _alvoReferences, .. NonFrameworkReferences(core)];

        NonFrameworkReferences(_accounts).ShouldAllBe(name => admitted.Contains(name) || name.StartsWith(IdentityModelPrefix));
    }

    /// <summary>
    /// The project file asks for exactly the two Alvo projects and the two packages — the second reading the dashboard's
    /// and the assistant's boundary suites make, because the compiler drops a reference nothing uses yet.
    /// </summary>
    [Fact]
    public void The_project_file_references_only_the_abstractions_the_core_and_the_two_IdentityModel_packages()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Accounts", "MMLib.Alvo.Accounts.csproj"));

        Includes(project, "ProjectReference").ShouldBe(
            ["../MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj", "../MMLib.Alvo/MMLib.Alvo.csproj"],
            ignoreOrder: true);
        Includes(project, "PackageReference").ShouldBe(
            ["Microsoft.IdentityModel.JsonWebTokens", "Microsoft.IdentityModel.Protocols.OpenIdConnect"],
            ignoreOrder: true);
    }

    /// <summary>The control for the grant facts: the scan sees a core member this assembly certainly references.</summary>
    [Fact]
    public void The_scan_sees_the_api_key_header_the_validation_reads()
        => _referenced.ShouldContain("MMLib.Alvo.Auth.AlvoAuthOptions::get_HeaderName");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));

    public static TheoryData<string> SecurityCoreMembers() => [.. InternalMemberReferences.SecurityCoreOnly];

    [Theory]
    [MemberData(nameof(SecurityCoreMembers))]
    public void It_never_reaches_a_security_core_member(string member)
        => _referenced.ShouldNotContain(member);

    private static string[] NonFrameworkReferences(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Where(reference => !IsSharedFramework(reference)).Select(reference => reference.Name!)];

    private static bool IsSharedFramework(AssemblyName reference)
        => _sharedFrameworkDirectories.Contains(Path.GetDirectoryName(Assembly.Load(reference).Location));

    private static string[] Includes(XDocument project, string item)
        => [.. project.Descendants(item).Select(element => (string)element.Attribute("Include")!)];
}
