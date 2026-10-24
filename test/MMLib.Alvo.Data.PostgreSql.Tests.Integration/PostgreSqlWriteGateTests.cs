using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL runs concurrent writers itself, so its stores queue at no gate: the write path is the one it always had.</summary>
public sealed class PostgreSqlWriteGateTests
{
    [Fact]
    public void Use_postgre_sql_hands_the_stores_no_write_gate()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UsePostgreSql("Host=localhost;Database=alvo_write_gate"));
        using var services = collection.BuildServiceProvider();

        services.GetRequiredService<RelationalConnectionFactory>().WriteGate.ShouldBeSameAs(NoRelationalWriteGate.Instance);
    }
}
