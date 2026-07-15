using NetArchTest.Rules;
using Shouldly;
using System.Reflection;
using Xunit;

namespace MMLib.Alvo.Abstractions.Tests;

public class ArchitectureTests
{
    private const string FamilyPrefix = "MMLib.Alvo";
    private const string AbstractionsAssemblyName = "MMLib.Alvo.Abstractions";

    [Fact]
    public void Abstractions_depends_on_no_other_project_in_the_solution()
    {
        var abstractions = Assembly.Load(AbstractionsAssemblyName);
        var siblingProjectReferences = SiblingProjectAssembliesReferencedBy(abstractions);

        siblingProjectReferences.ShouldBeEmpty(
            "MMLib.Alvo.Abstractions must not depend on any other project in the "
            + $"solution, but references: {string.Join(", ", siblingProjectReferences)}."
            + OffendingTypeDetail(abstractions, siblingProjectReferences));
    }

    private static string[] SiblingProjectAssembliesReferencedBy(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith(FamilyPrefix, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .ToArray();

    private static string OffendingTypeDetail(Assembly assembly, string[] siblingProjectReferences)
    {
        if (siblingProjectReferences.Length == 0)
        {
            return string.Empty;
        }

        var failingTypes = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(siblingProjectReferences)
            .GetResult()
            .FailingTypes ?? Enumerable.Empty<Type>();

        var offendingTypeNames = failingTypes.Select(type => type.FullName).ToArray();

        return offendingTypeNames.Length == 0
            ? string.Empty
            : $" Offending types: {string.Join(", ", offendingTypeNames)}.";
    }
}
