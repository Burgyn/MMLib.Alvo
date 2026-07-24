using NetArchTest.Rules;

namespace MMLib.Alvo.Tests.Architecture;

/// <summary>
/// Architecture rules that must hold for EVERY MMLib.Alvo production assembly.
/// Linked (not referenced) into each test project via <c>test/Directory.Build.props</c>,
/// so it compiles into each test assembly and runs against that project's sibling
/// production assembly (<see cref="TestTarget"/>). Opt a project out with
/// <c>AlvoSharedArchTests=false</c> where it does not map 1:1 to a production
/// assembly (conventions, integration, e2e).
/// </summary>
public class SharedArchitectureRules
{
    private const string InternalNamespaceSegmentPattern = @"(^|\.)Internal(\.|$)";
    private const string CoreAssemblyName = "MMLib.Alvo";
    private const string AbstractionsAssemblyName = "MMLib.Alvo.Abstractions";

    [Fact]
    public void Public_types_do_not_live_in_internal_namespaces()
    {
        var result = Types.InAssembly(TestTarget.Resolve())
            .That().ResideInNamespaceMatching(InternalNamespaceSegmentPattern)
            .ShouldNot().BePublic()
            .GetResult();

        var offenders = (result.FailingTypes ?? Enumerable.Empty<Type>()).Select(type => type.FullName);
        result.IsSuccessful.ShouldBeTrue(
            "Types in a '*.Internal' namespace must not be public. Offending types: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The EF-shield: the core must reference only <c>MMLib.Alvo.Abstractions</c>
    /// plus framework/system assemblies — never EF Core or Npgsql. EF lives
    /// exclusively in the Data.* packages behind ISchemaMigrator.
    /// </summary>
    /// <remarks>
    /// Unlike the other facts here, this invariant is about one specific
    /// assembly (<c>MMLib.Alvo</c>), not "whichever sibling this test project
    /// targets". It still resolves via <see cref="TestTarget"/> — the sibling
    /// that every other shared-arch-enabled project already has a working
    /// reference to — and no-ops unless that sibling is the core itself, so
    /// it does not attempt to <c>Assembly.Load("MMLib.Alvo")</c> from test
    /// projects (e.g. Schema.Tests, Abstractions.Tests) that never reference
    /// it and would fail to resolve it.
    /// </remarks>
    [Fact]
    public void Core_depends_only_on_Abstractions()
    {
        var core = TestTarget.Resolve();
        if (core.GetName().Name != CoreAssemblyName)
        {
            return;
        }

        var referencedAssemblyNames = core.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        var forbiddenEfReferences = referencedAssemblyNames
            .Where(name =>
                name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || name.StartsWith("Npgsql", StringComparison.Ordinal))
            .ToArray();

        forbiddenEfReferences.ShouldBeEmpty(
            $"{CoreAssemblyName} must stay EF-free — EF lives only in Data.* packages behind "
            + $"ISchemaMigrator. Offending references: {string.Join(", ", forbiddenEfReferences)}.");

        var unexpectedFamilyReferences = referencedAssemblyNames
            .Where(name => name.StartsWith(CoreAssemblyName, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .Where(name => name != CoreAssemblyName)
            .ToArray();

        unexpectedFamilyReferences.ShouldBeEmpty(
            $"{CoreAssemblyName} must depend on no other MMLib.Alvo.* assembly besides "
            + $"{AbstractionsAssemblyName}. Offending references: "
            + string.Join(", ", unexpectedFamilyReferences));
    }
}
