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
    // Wired from the first commit; green today (Abstractions is empty) and it
    // fails the moment Abstractions references any sibling MMLib.Alvo.* assembly.
    [Fact]
    public void Abstractions_depends_on_no_other_project_in_the_solution()
    {
        var abstractions = Assembly.Load(AbstractionsAssemblyName);

        // The reliable signal: GetReferencedAssemblies() lists only assemblies
        // actually used by a type in Abstractions (the compiler drops unused
        // references), so any MMLib.Alvo.* entry other than itself is a genuine,
        // false-positive-free cross-project dependency.
        var forbiddenSiblings = abstractions
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith(FamilyPrefix, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .ToArray();

        // Best-effort enrichment: NetArchTest names the offending types for a
        // helpful failure message. Diagnostics only — the assembly-level check
        // above is the source of truth for pass/fail.
        var offendingTypes = string.Empty;
        if (forbiddenSiblings.Length > 0)
        {
            var result = Types.InAssembly(abstractions)
                .Should()
                .NotHaveDependencyOnAny(forbiddenSiblings)
                .GetResult();

            var failing = (result.FailingTypes ?? Enumerable.Empty<Type>())
                .Select(t => t.FullName)
                .ToArray();

            if (failing.Length > 0)
            {
                offendingTypes = $" Offending types: {string.Join(", ", failing)}.";
            }
        }

        // Always asserts (no vacuous early return): empty today, and a real
        // failure the moment a sibling reference appears.
        forbiddenSiblings.ShouldBeEmpty(
            "MMLib.Alvo.Abstractions must not depend on any other project in the " +
            $"solution, but references: {string.Join(", ", forbiddenSiblings)}.{offendingTypes}");
    }
}
