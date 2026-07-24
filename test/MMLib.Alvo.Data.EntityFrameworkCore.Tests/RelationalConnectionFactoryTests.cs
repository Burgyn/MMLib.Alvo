using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class RelationalConnectionFactoryTests
{
    [Fact]
    public void Create_returns_a_fresh_connection_each_call()
    {
        var factory = new RelationalConnectionFactory(() => new SqliteConnection("Data Source=:memory:"));

        using var a = factory.Create();
        using var b = factory.Create();

        a.ShouldNotBeSameAs(b);
    }
}
