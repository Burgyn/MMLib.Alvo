using System.Reflection;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Placeholder so this Testcontainers-backed project has at least one test
/// (an empty run fails <c>dotnet test</c>) until Task 12 wires the PostgreSQL
/// provider and adds real container-backed integration tests here.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void Data_PostgreSql_assembly_is_reachable()
    {
        var assembly = Assembly.Load("MMLib.Alvo.Data.PostgreSql");

        assembly.ShouldNotBeNull();
    }
}
