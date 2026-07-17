using NetArchTest.Rules;
using System.Reflection;

namespace MMLib.Alvo.Tests.Architecture;

/// <summary>
/// Architecture rules that must hold for EVERY MMLib.Alvo production assembly.
/// This file is <em>linked</em> (not referenced) into each test project via
/// <c>test/Directory.Build.props</c>, so it compiles into each test assembly and
/// runs against that project's sibling production assembly. Resolve the target as
/// the test assembly name minus the <c>.Tests</c> suffix, or an explicit
/// <see cref="ArchTargetAttribute"/>. Opt a project out with
/// <c>AlvoSharedArchTests=false</c> (for projects that don't map 1:1 to a
/// production assembly, e.g. the conventions, integration, and e2e projects).
/// </summary>
public class SharedArchitectureRules
{
    private const string TestsSuffix = ".Tests";
    private const string InternalNamespaceSegmentPattern = @"\.Internal(\.|$)";

    private static Assembly TargetAssembly()
    {
        var testAssembly = Assembly.GetExecutingAssembly();
        var explicitTarget = testAssembly.GetCustomAttribute<ArchTargetAttribute>()?.TargetAssemblyName;
        var targetName = explicitTarget ?? StripTestsSuffix(testAssembly.GetName().Name!);
        try
        {
            return Assembly.Load(targetName);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Shared architecture rules could not load target assembly '{targetName}' "
                + $"(inferred from test assembly '{testAssembly.GetName().Name}'). Either reference the "
                + "production project it tests, point at it explicitly with [assembly: ArchTarget(\"...\")] , "
                + "or opt this project out via <AlvoSharedArchTests>false</AlvoSharedArchTests>.",
                exception);
        }
    }

    private static string StripTestsSuffix(string assemblyName) =>
        assemblyName.EndsWith(TestsSuffix, StringComparison.Ordinal)
            ? assemblyName[..^TestsSuffix.Length]
            : assemblyName;

    [Fact]
    public void Public_types_do_not_live_in_internal_namespaces()
    {
        var result = Types.InAssembly(TargetAssembly())
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
