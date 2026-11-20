using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The tenant guard's (#161) DDL, introspection and ordinary migrations, one set of facts for both engines: a scoped reference
/// spans <c>(tenant_id, id)</c>, what the database reports back is the reference, never the tenant column, and a descriptor
/// change that adds, re-shapes, renames or drops a guard leaves a database that still refuses another tenant's row.
/// </summary>
/// <remarks>
/// The migration facts judge the database through raw statements on a connection of their own, because a guard is a claim
/// about the engine: a row of tenant <c>north</c> naming tenant <c>south</c>'s work order must be refused by the engine itself.
/// </remarks>
public abstract class TenantGuardMigrationFacts
{
    private static readonly Guid _north = Guid.NewGuid();

    private static readonly Guid _south = Guid.NewGuid();

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

    /// <summary>
    /// A scoped reference added to tables that already exist — the target populated — is an ordinary model diff that adds the
    /// target's <c>(tenant_id, id)</c> key and the guard, and the guard refuses another tenant's row under every action.
    /// </summary>
    /// <param name="onDelete">The added reference's action.</param>
    [Theory]
    [InlineData(OnDelete.Restrict)]
    [InlineData(OnDelete.Cascade)]
    [InlineData(OnDelete.SetNull)]
    public async Task A_scoped_reference_added_to_existing_tables_gets_its_guard(OnDelete onDelete)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.Unreferenced());
        await InsertParentAsync("work_orders", _north);
        await InsertParentAsync("work_orders", _south);
        await MigrateAsync(TenantGuardSchemas.Unreferenced(), TenantGuardSchemas.Pair(onDelete));

        await ShouldBeGuardedAsync(onDelete);
    }

    /// <summary>
    /// Changing a guarded reference's <c>onDelete</c> re-shapes the guard — on SQLite <c>setNull</c> is a single-column key
    /// beside the guard, so <c>restrict</c> ↔ <c>setNull</c> swaps one key for two — and the new action is the one that runs.
    /// </summary>
    /// <param name="from">The action the database holds.</param>
    /// <param name="to">The action the descriptor now declares.</param>
    [Theory]
    [InlineData(OnDelete.Restrict, OnDelete.SetNull)]
    [InlineData(OnDelete.SetNull, OnDelete.Restrict)]
    [InlineData(OnDelete.SetNull, OnDelete.Cascade)]
    [InlineData(OnDelete.Cascade, OnDelete.Restrict)]
    public async Task Changing_a_guarded_references_on_delete_reshapes_its_guard(OnDelete from, OnDelete to)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.Pair(from));
        await MigrateAsync(TenantGuardSchemas.Pair(from), TenantGuardSchemas.Pair(to));

        (await Migrator.PlanAsync(TenantGuardSchemas.Pair(to), TenantGuardSchemas.Pair(to), new MigrationOptions(), Ct)).IsEmpty.ShouldBeTrue();
        await ShouldBeGuardedAsync(to);
    }

    /// <summary>
    /// Flipping either end of a guarded reference to global drops the guard — the reference is then a plain key on
    /// <c>id</c> — and flipping it back adds the guard again.
    /// </summary>
    /// <param name="entity">The end whose tenancy flips.</param>
    [Theory]
    [InlineData("work_orders")]
    [InlineData("visits")]
    public async Task Flipping_an_end_to_global_and_back_drops_and_restores_the_guard(string entity)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");
        var guarded = TenantGuardSchemas.Pair(OnDelete.Restrict);

        await MigrateAsync(new SchemaModel([]), guarded);
        await MigrateAsync(guarded, TenantGuardSchemas.Flipped(entity));
        (await IntrospectReferenceAsync("visits", "work_order_id")).ShouldBe(new RefSchema("work_orders", OnDelete.Restrict));
        await MigrateAsync(TenantGuardSchemas.Flipped(entity), guarded);

        await ShouldBeGuardedAsync(OnDelete.Restrict);
    }

    /// <summary>
    /// Renaming the guarded field, its target, or both carries the guard through the renames: the next plan is empty, the
    /// guard still refuses another tenant's row, and a later descriptor that drops the reference drops the guard and the
    /// target's key by the names the database really holds — leaving no constraint behind.
    /// </summary>
    /// <param name="renames"><c>entity</c>, <c>field</c> or <c>both</c>.</param>
    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_renamed_guard_replans_empty_still_guards_and_drops_cleanly(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");
        var renamed = TenantGuardSchemas.Renamed(renames);
        var target = renamed.Entities[0].Name;
        var field = renamed.Entities[1].Fields[1].Name;

        await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.Restrict));
        await MigrateAsync(TenantGuardSchemas.Pair(OnDelete.Restrict), renamed);

        (await Migrator.PlanAsync(renamed, renamed, new MigrationOptions(), Ct)).IsEmpty.ShouldBeTrue();
        await ShouldBeGuardedAsync(OnDelete.Restrict, target, field);

        await MigrateAsync(renamed, TenantGuardSchemas.RenamedWithoutTheReference(renames));
        (await RawAsync($"""DELETE FROM "{target}" """)).ShouldBeNull("no constraint survives the reference that declared it");
    }

    /// <summary>
    /// Asserts the guard on <c>visits.<paramref name="field"/></c> refuses a <c>north</c> row naming <c>south</c>'s parent,
    /// stores one naming <c>north</c>'s own, and that deleting that parent does what <paramref name="onDelete"/> says —
    /// <c>setNull</c> clearing the reference and keeping the row's tenant.
    /// </summary>
    private async Task ShouldBeGuardedAsync(OnDelete onDelete, string target = "work_orders", string field = "work_order_id")
    {
        var ours = await InsertParentAsync(target, _north);
        var theirs = await InsertParentAsync(target, _south);
        var visit = Guid.NewGuid();

        (await InsertVisitAsync(field, theirs, Guid.NewGuid())).ShouldNotBeNull("a reference to another tenant's row is refused");
        (await InsertVisitAsync(field, ours, visit)).ShouldBeNull("a reference to the tenant's own row is stored");

        var deleted = await RawAsync($"""DELETE FROM "{target}" WHERE "id" = '{ours}'""");
        if (onDelete == OnDelete.Restrict)
        {
            deleted.ShouldNotBeNull("restrict refuses the delete");
            return;
        }

        deleted.ShouldBeNull();
        var remaining = onDelete == OnDelete.Cascade ? 0 : 1;
        (await ScalarAsync<int>($"""SELECT CAST(COUNT(*) AS INTEGER) FROM "visits" WHERE "id" = '{visit}' AND "{field}" IS NULL AND "tenant_id" = '{_north}'"""))
            .ShouldBe(remaining, "cascade removes the visit; setNull clears its reference and keeps its tenant");
    }

    private async Task<Guid> InsertParentAsync(string table, Guid tenant)
    {
        var id = Guid.NewGuid();
        (await RawAsync($"""INSERT INTO "{table}" ("id", "tenant_id", "title") VALUES ('{id}', '{tenant}', 'WO')""")).ShouldBeNull();
        return id;
    }

    private Task<Exception?> InsertVisitAsync(string field, Guid parent, Guid id) =>
        RawAsync($"""INSERT INTO "visits" ("id", "tenant_id", "{field}") VALUES ('{id}', '{_north}', '{parent}')""");

    /// <summary>Runs a one-column, one-row query on a connection of its own and returns its value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="sql">The query; identifiers double-quoted.</param>
    protected async Task<T> ScalarAsync<T>(string sql)
    {
        using var context = Services.GetRequiredService<AlvoDataContextFactory>().Create();
        var scalar = $"SELECT ({sql}) AS \"Value\"";
        return await context.Database.SqlQueryRaw<T>(scalar).SingleAsync(Ct);
    }

    private Task<SchemaModel> IntrospectAsync() => Services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync(Ct);

    private async Task<RefSchema?> IntrospectReferenceAsync(string entity, string field) =>
        (await IntrospectAsync()).Entities.Single(e => e.Name == entity).Fields.Single(f => f.Name == field).Reference;
}
