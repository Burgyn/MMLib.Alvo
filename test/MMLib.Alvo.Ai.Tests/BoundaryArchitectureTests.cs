using MMLib.Alvo.Ai;

using System.Reflection;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The package boundary this whole design rests on.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>MMLib.Alvo.Ai</c> must not reference <c>MMLib.Alvo</c>.</b> Everything it needs from the core
/// arrives as <see cref="MMLib.Alvo.Management.IAlvoManagement"/> and
/// <see cref="IAiConnectionResolver"/>, which is what makes "the agent is a client of the Management API,
/// like the dashboard and the CLI" a structural fact rather than a promise. An agent that could reach into
/// the core would eventually reach past the Management API and grow a capability no other client has — and
/// in this package, the capability it would grow is the apply it deliberately does not have.
/// </para>
/// <para>
/// <b>Checked against the loaded assembly rather than the project file</b>, the way
/// <c>MMLib.Alvo.Admin.Tests</c> checks the same rule: a project file names what was asked for and an
/// assembly names what was linked, so a transitive reference arriving through a third project would satisfy
/// the first check and break the rule.
/// </para>
/// </remarks>
public sealed class BoundaryArchitectureTests
{
    private static readonly AssemblyName[] _references =
        typeof(AlvoAssistant).Assembly.GetReferencedAssemblies();

    [Fact]
    public void The_agent_does_not_reference_the_core()
        => _references.Select(reference => reference.Name).ShouldNotContain("MMLib.Alvo");

    [Fact]
    public void The_agent_reaches_the_core_only_through_the_abstractions()
        => _references.Select(reference => reference.Name).ShouldContain("MMLib.Alvo.Abstractions");

    /// <summary>The agent must not acquire the dashboard or the identity package either.</summary>
    /// <remarks>
    /// Neither is named by the design, and both follow from the same argument: an agent that referenced the
    /// dashboard could only ever be hosted beside it, and one that referenced identity would carry EF Core
    /// and ASP.NET Core Identity into a package whose whole job is to talk to one endpoint.
    /// </remarks>
    [Theory]
    [InlineData("MMLib.Alvo.Admin")]
    [InlineData("MMLib.Alvo.Identity")]
    [InlineData("MMLib.Alvo.Data.EntityFrameworkCore")]
    public void The_agent_references_no_other_alvo_package(string package)
        => _references.Select(reference => reference.Name).ShouldNotContain(package);

    /// <summary>
    /// The project file asks for exactly the one Alvo project the rule admits.
    /// </summary>
    /// <remarks>
    /// The second reading the dashboard's own boundary test makes, for the reason it gives: the assembly
    /// check above catches what was linked, and this catches a reference somebody added that the compiler
    /// then optimised away — which would pass the first check and be a standing invitation to use it.
    /// </remarks>
    [Fact]
    public void The_project_file_references_only_the_abstractions()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "MMLib.Alvo.Ai", "MMLib.Alvo.Ai.csproj"));

        project.ShouldContain("MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj");
        project.ShouldNotContain("MMLib.Alvo/MMLib.Alvo.csproj");
    }
}
