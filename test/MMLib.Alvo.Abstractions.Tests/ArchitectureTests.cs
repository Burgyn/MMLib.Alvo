using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Abstractions.Tests;

public class ArchitectureTests
{
    // Every project that ships as its own assembly shares this root prefix.
    private const string FamilyPrefix = "MMLib.Alvo";
    private const string AbstractionsAssemblyName = "MMLib.Alvo.Abstractions";

    // Architectural invariant (spec §1.1): MMLib.Alvo.Abstractions is the root of
    // the dependency graph — it may depend on NO other project in the solution.
    // Wired from the first commit; stays green today (Abstractions is empty) and
    // becomes load-bearing the moment a type here references a sibling package.
    [Fact]
    public void Abstractions_depends_on_no_other_project_in_the_solution()
    {
        var abstractions = Assembly.Load(AbstractionsAssemblyName);

        // Sibling MMLib.Alvo.* assemblies that Abstractions actually references
        // (everything except itself). This set must always be empty.
        var forbiddenSiblings = abstractions
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith(FamilyPrefix, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .ToArray();

        if (forbiddenSiblings.Length == 0)
        {
            // No sibling references at the assembly level — invariant holds.
            return;
        }

        // A sibling is referenced: use NetArchTest to name the offending types.
        var result = Types.InAssembly(abstractions)
            .Should()
            .NotHaveDependencyOnAny(forbiddenSiblings)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "MMLib.Alvo.Abstractions must not depend on any other project in the " +
            $"solution, but references: {string.Join(", ", forbiddenSiblings)}. " +
            "Offending types: " +
            string.Join(", ", (result.FailingTypes ?? Enumerable.Empty<Type>())
                .Select(t => t.FullName)));
    }
}
