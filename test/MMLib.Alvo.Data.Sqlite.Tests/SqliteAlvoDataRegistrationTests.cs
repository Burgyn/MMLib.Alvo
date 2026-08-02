using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The wiring fact: a host that only ever calls the public entry point gets a working data port. Until this
/// registration existed the port could be constructed by hand in a test and still be unreachable from an
/// application — which is how a security core ships inert.
/// </summary>
public class SqliteAlvoDataRegistrationTests
{
    [Fact]
    public void The_public_entry_point_alone_yields_a_resolvable_data_port()
    {
        using var services = Build();

        services.GetRequiredService<IAlvoData>().ShouldNotBeNull();
    }

    [Fact]
    public void The_driver_supplies_its_own_field_renderer_and_dialect()
    {
        using var services = Build();

        services.GetRequiredService<IFieldSqlRenderer>().ShouldBeOfType<SqliteFieldSqlRenderer>();
        services.GetRequiredService<IAlvoSqlDialect>().ShouldBeOfType<SqliteSqlDialect>();
    }

    /// <summary>
    /// The port must be the driver's own, not one composed from whatever renderer happened to be registered
    /// first: a data port holding a different engine's dialect would render syntactically valid SQL for the
    /// wrong engine, and on the <c>USING</c> path a statement that fails to parse is the good outcome.
    /// </summary>
    [Fact]
    public void The_data_port_is_composed_from_the_drivers_own_renderers()
    {
        using var services = Build();

        var data = services.GetRequiredService<IAlvoData>();

        Collaborator<IFieldSqlRenderer>(data).ShouldBeSameAs(services.GetRequiredService<IFieldSqlRenderer>());
        Collaborator<IAlvoSqlDialect>(data).ShouldBeSameAs(services.GetRequiredService<IAlvoSqlDialect>());
    }

    /// <summary>
    /// Registration is idempotent, so a host that attaches the provider twice (or attaches one and then
    /// overrides a service) does not end up with two data ports disagreeing about the dialect.
    /// </summary>
    [Fact]
    public void Attaching_the_provider_twice_registers_one_data_port()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:").UseSqlite("Data Source=:memory:"));

        collection.Count(service => service.ServiceType == typeof(IAlvoData)).ShouldBe(1);
    }

    /// <summary>
    /// A host that registers its own renderer before the driver keeps it — <c>TryAdd</c> means the driver
    /// supplies a default, not an override. Named because the alternative (the driver winning) would make an
    /// out-of-repo dialect impossible to substitute.
    /// </summary>
    [Fact]
    public void A_host_supplied_dialect_wins_over_the_drivers_default()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAlvoSqlDialect>(new SqliteSqlDialect());
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));

        collection.Count(service => service.ServiceType == typeof(IAlvoSqlDialect)).ShouldBe(1);
    }

    private static T Collaborator<T>(IAlvoData data) => data.GetType()
        .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Select(field => field.GetValue(data))
        .SelectMany(Reachable)
        .OfType<T>()
        .First();

    /// <summary>
    /// The renderers reach the port through one collaborator (the read-statement composer), so the search
    /// looks one level past <see cref="EfAlvoData"/>'s own fields. Deliberately shallow: a deeper walk would
    /// find a renderer no matter where it was wired, which is the opposite of what this pins.
    /// </summary>
    private static IEnumerable<object?> Reachable(object? value) => value is null
        ? []
        : [value, .. value.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.GetValue(value))];

    private static ServiceProvider Build()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
        return collection.BuildServiceProvider();
    }
}
