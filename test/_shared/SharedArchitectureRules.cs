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

    [Fact(Skip = "Enabled once the core project (MMLib.Alvo) exists — F3.")]
    public void Core_depends_only_on_Abstractions()
    {
    }
}
