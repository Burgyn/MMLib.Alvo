using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The tenant guard's (#161) DDL and introspection, one set of facts for both engines: a scoped reference spans
/// <c>(tenant_id, id)</c>, and what the database reports back is the reference, never the tenant column.
/// </summary>
public abstract class TenantGuardMigrationFacts
{
    /// <summary>The container the engine's leg builds, with the driver registered.</summary>
    protected abstract IServiceProvider Services { get; }

    /// <summary>The migrator under test.</summary>
    protected ISchemaMigrator Migrator => Services.GetRequiredService<ISchemaMigrator>();

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Whether the leg can run at all (PostgreSQL needs a Linux Docker daemon).</summary>
    protected virtual bool Runnable => true;

    /// <summary>Plans and applies <paramref name="from"/> to <paramref name="to"/>, destructive changes allowed.</summary>
    /// <param name="from">The schema the database holds.</param>
    /// <param name="to">The schema to reach.</param>
    protected async Task MigrateAsync(SchemaModel from, SchemaModel to)
    {
        var options = new MigrationOptions { AllowDestructive = true };
        var plan = await Migrator.PlanAsync(from, to, options, Ct);
        await Migrator.ApplyAsync(plan, options, Ct);
    }

    /// <summary>Runs one raw statement on a connection of its own and returns the engine's failure, if any.</summary>
    /// <param name="sql">The statement; identifiers double-quoted.</param>
    protected Task<Exception?> RawAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(Services.GetRequiredService<AlvoDataContextFactory>(), sql, Ct);

    [Fact]
    public async Task A_scoped_pair_is_created_with_its_tenant_guard()
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.AllActions(), new MigrationOptions(), Ct);
        await Verify(string.Join("\n;\n", plan.Sql));
    }

    [Fact]
    public async Task Introspecting_a_guarded_reference_yields_the_field_and_never_the_tenant_column()
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.AllActions());
        var live = await Services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync(Ct);

        foreach (var (child, onDelete) in new[] { ("visits", OnDelete.Restrict), ("tasks", OnDelete.Cascade), ("notes", OnDelete.SetNull) })
        {
            var entity = live.Entities.Single(e => e.Name == child);
            entity.Fields.Single(f => f.Name == "work_order_id").Reference.ShouldBe(new RefSchema("work_orders", onDelete));
            entity.Fields.Single(f => f.Name == "tenant_id").Type.ShouldNotBe(FieldType.Ref);
        }
    }
}
