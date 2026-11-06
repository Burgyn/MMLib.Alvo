using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The DDL an exclusion becomes on PostgreSQL, frozen: one <c>EXCLUDE USING gist</c> table constraint — equality columns
/// <c>WITH =</c>, the half-open range <c>WITH &amp;&amp;</c> — carrying the index's <c>where</c>. Skipped on Windows runners
/// like its siblings.
/// </summary>
public sealed class PostgreSqlExclusionDdlTests
{
    [Fact]
    public Task An_exclusion_is_one_gist_constraint_with_its_where()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

        var sql = ((IAlvoSqlDialect)new PostgreSqlSqlDialect()).ExclusionConstraint(Definition());

        return Verify(Text(sql.ShouldNotBeNull()));
    }

    private static ExclusionConstraintDefinition Definition()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        using var provider = services.BuildServiceProvider();
        var filters = new IndexFilterSql(
            provider.GetRequiredService<ICelCompiler>(), provider.GetRequiredService<IPredicateRenderer>(), new PostgreSqlFieldSqlRenderer());

        return ExclusionDefinitions.For(Bookings, Bookings.Indexes[0], filters);
    }

    private static string Text(ExclusionConstraintSql sql) =>
        $"-- create\n{string.Join("\n;\n", sql.Create)}\n-- drop\n{string.Join("\n;\n", sql.Drop)}\n-- survives a table rebuild: {sql.SurvivesTableRebuild}";

    private static EntitySchema Bookings => new()
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
