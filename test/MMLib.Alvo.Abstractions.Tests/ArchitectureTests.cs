using NetArchTest.Rules;
using System.Reflection;

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

    /// <summary>
    /// The milestone plan adds ASP.NET to the core in PR3 (the HTTP Data API) — never to
    /// Abstractions. This keeps the port assembly (the contracts PR2's SQLite/PostgreSQL
    /// providers and any future host implement) free of every concrete infrastructure
    /// dependency: ASP.NET Core, EF Core, Npgsql, and ADO.NET's own <c>System.Data</c>.
    /// </summary>
    [Fact]
    public void Abstractions_stays_free_of_asp_net_and_data_access()
    {
        var abstractions = Assembly.Load(AbstractionsAssemblyName);
        var referenced = abstractions.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();

        referenced.ShouldNotContain(name =>
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.StartsWith("System.Data", StringComparison.Ordinal),
            $"MMLib.Alvo.Abstractions must not reference ASP.NET/EF Core/Npgsql/System.Data, but references: {string.Join(", ", referenced)}.");
    }
}
