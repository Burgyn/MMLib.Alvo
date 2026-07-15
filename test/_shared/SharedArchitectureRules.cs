using MMLib.Alvo.Testing;
using NetArchTest.Rules;
using Shouldly;
using System.Reflection;
using Xunit;

namespace MMLib.Alvo.Tests.Shared;

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

    private static Assembly TargetAssembly()
    {
        var testAssembly = Assembly.GetExecutingAssembly();
        var explicitTarget = testAssembly.GetCustomAttribute<ArchTargetAttribute>()?.TargetAssemblyName;
        var targetName = explicitTarget ?? StripTestsSuffix(testAssembly.GetName().Name!);
        return Assembly.Load(targetName);
    }

    private static string StripTestsSuffix(string assemblyName) =>
        assemblyName.EndsWith(TestsSuffix, StringComparison.Ordinal)
            ? assemblyName[..^TestsSuffix.Length]
            : assemblyName;

    [Fact]
    public void Public_types_do_not_live_in_internal_namespaces()
    {
        var result = Types.InAssembly(TargetAssembly())
            .That().ResideInNamespaceContaining(".Internal")
            .ShouldNot().BePublic()
            .GetResult();

        var offenders = (result.FailingTypes ?? Enumerable.Empty<Type>()).Select(type => type.FullName);
        result.IsSuccessful.ShouldBeTrue(
            "Types in a '*.Internal' namespace must not be public. Offending types: "
            + string.Join(", ", offenders));
    }

    [Fact(Skip = "Ožije keď vznikne core projekt MMLib.Alvo — F3.")]
    public void Core_depends_only_on_Abstractions()
    {
        // F3: the core assembly must not depend on any MMLib.Alvo.* assembly
        // other than MMLib.Alvo.Abstractions. No core project exists yet.
    }
}
