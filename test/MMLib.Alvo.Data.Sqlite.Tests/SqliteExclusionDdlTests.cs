using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The DDL an exclusion becomes on SQLite (HX-12 D2), frozen: an ordinary index the overlap check seeks, and an
/// <c>AFTER INSERT</c> and an <c>AFTER UPDATE</c> trigger that raise a tagged abort when the written row overlaps another
/// over one shared overlap join.
/// </summary>
public sealed class SqliteExclusionDdlTests
{
    [Fact]
    public Task An_exclusion_is_an_insert_and_an_update_trigger_over_one_overlap_join()
    {
        var sql = ((IAlvoSqlDialect)new SqliteSqlDialect()).ExclusionConstraint(Bookings.Definition(new SqliteFieldSqlRenderer()));

        return Verify(Bookings.Text(sql.ShouldNotBeNull()));
    }
}

/// <summary>The exclusion both engines' DDL goldens freeze: scoped bookings, one room at a time, cancelled ones excepted.</summary>
internal static class Bookings
{
    internal static ExclusionConstraintDefinition Definition(IFieldSqlRenderer fields)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        using var provider = services.BuildServiceProvider();
        var filters = new IndexFilterSql(provider.GetRequiredService<ICelCompiler>(), provider.GetRequiredService<IPredicateRenderer>(), fields);

        return ExclusionDefinitions.For(Entity, Entity.Indexes[0], filters);
    }

    internal static string Text(ExclusionConstraintSql sql) =>
        $"-- create\n{string.Join("\n;\n", sql.Create)}\n-- drop\n{string.Join("\n;\n", sql.Drop)}\n-- survives a table rebuild: {sql.SurvivesTableRebuild}";

    private static EntitySchema Entity => new()
    {
        Name = "bookings",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "room", Type = FieldType.Ref, Required = true, Reference = new RefSchema("rooms", OnDelete.Restrict) },
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Required = true, CheckExpression = "ends_at >= starts_at" },
            new FieldSchema { Name = "status", Type = FieldType.Enum, Nullable = true, EnumValues = ["held", "confirmed", "cancelled"] },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
        ],
        Indexes = [new IndexSchema(["room"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at"), Where = "status != 'cancelled'" }],
    };
}
