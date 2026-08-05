using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// An <see cref="ISchemaMigrator"/> that reuses EF Core's migrations differ and per-provider SQL
/// generator to turn a (current, desired) pair of <see cref="SchemaModel"/>s into a
/// <see cref="MigrationPlan"/>, and executes the resulting plan over a fresh, per-call ADO.NET
/// connection.
/// </summary>
/// <remarks>
/// The provider-specific services (<see cref="IMigrationsModelDiffer"/>,
/// <see cref="IMigrationsSqlGenerator"/>, <see cref="IModelRuntimeInitializer"/>) and the
/// conventionless <see cref="ModelBuilder"/> factory are injected by the provider wiring
/// (SQLite/PostgreSQL packages), so this type only depends on EFCore.Relational abstractions and
/// stays provider-agnostic. The <see cref="DbConnection"/> is likewise provider-supplied: plain
/// ADO.NET is enough to execute the already-generated SQL, so no relational-command infrastructure
/// is needed here.
///
/// <para>
/// <see cref="ApplyAsync"/> opens a fresh connection from the injected
/// <see cref="RelationalConnectionFactory"/> for each call, scoped to a <c>using</c> block that
/// opens it, runs the whole plan in one transaction, and disposes the connection when done. This
/// gives two concurrent callers independent connections/transactions instead of serializing on one
/// shared connection — the shape runtime concurrent schema changes (PR-B) need. Concurrency control
/// across independent clients changing the schema at runtime is governed by descriptor optimistic
/// locking (PR-B), not by this type.
/// </para>
/// </remarks>
public sealed class EfCoreSchemaMigrator : ISchemaMigrator
{
    private readonly IMigrationsModelDiffer _differ;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IModelRuntimeInitializer _modelRuntimeInitializer;
    private readonly Func<ModelBuilder> _newModelBuilder;
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly ComputedColumnSql? _computed;

    /// <summary>
    /// Initializes a new migrator from a provider's EF Core services and a per-call connection factory.
    /// </summary>
    /// <param name="differ">EF Core's provider-flavored migrations model differ.</param>
    /// <param name="sqlGenerator">EF Core's provider-flavored migrations SQL generator.</param>
    /// <param name="modelRuntimeInitializer">Runs the runtime-model initialization the relational model requires.</param>
    /// <param name="newModelBuilder">Creates a conventionless <see cref="ModelBuilder"/> seeded with the provider's convention set.</param>
    /// <param name="connections">Creates a fresh ADO.NET connection per <see cref="ApplyAsync"/> call; each connection is owned and disposed within that call.</param>
    /// <param name="dialect">
    /// This driver's SQL seam, asked two questions about a generated column: whether the engine can express one
    /// at all, and whether adding one to an existing table needs the table rebuilt.
    /// </param>
    /// <param name="computed">
    /// Renders a <c>computed</c> field's CEL to this driver's SQL, or <see langword="null"/> when the container
    /// registered no expression services — a schema declaring one is then refused by name.
    /// </param>
    internal EfCoreSchemaMigrator(
        IMigrationsModelDiffer differ,
        IMigrationsSqlGenerator sqlGenerator,
        IModelRuntimeInitializer modelRuntimeInitializer,
        Func<ModelBuilder> newModelBuilder,
        RelationalConnectionFactory connections,
        IAlvoSqlDialect dialect,
        ComputedColumnSql? computed)
    {
        ArgumentNullException.ThrowIfNull(differ);
        ArgumentNullException.ThrowIfNull(sqlGenerator);
        ArgumentNullException.ThrowIfNull(modelRuntimeInitializer);
        ArgumentNullException.ThrowIfNull(newModelBuilder);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);

        _differ = differ;
        _sqlGenerator = sqlGenerator;
        _modelRuntimeInitializer = modelRuntimeInitializer;
        _newModelBuilder = newModelBuilder;
        _connections = connections;
        _dialect = dialect;
        _computed = computed;
    }

    /// <inheritdoc/>
    public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        ct.ThrowIfCancellationRequested();

        var (prePass, desiredModel, residual) = ComputeResidualDiff(current, desired);
        var operations = AssembleOperations(prePass, residual);
        var steps = BuildSemanticSteps(prePass, residual);
        var sql = GenerateSql(operations, desiredModel);

        return Task.FromResult(new MigrationPlan { Steps = steps, Sql = sql });
    }

    // Pre-applies the descriptor's DECLARED renames to the current schema so the differ sees the
    // renamed members already aligned by name (no drop+add), diffs the aligned pair, then
    // neutralizes EF's GUESSED renames: any rename op left in the residual (i.e. not one of our
    // declared, pre-aligned renames) is a heuristic pairing of an unrelated drop+add and must be
    // split back into its destructive Drop + fresh Add, or it would bypass the destructive
    // guardrail and silently carry data into a semantically-unrelated column.
    private (RenamePrePass.Result PrePass, IModel DesiredModel, IReadOnlyList<MigrationOperation> Residual) ComputeResidualDiff(
        SchemaModel current, SchemaModel desired)
    {
        var prePass = RenamePrePass.Compute(current, desired);
        var currentModel = BuildInitializedModel(prePass.AlignedCurrent);
        var desiredModel = BuildInitializedModel(desired);

        var residual = _differ.GetDifferences(currentModel.GetRelationalModel(), desiredModel.GetRelationalModel());
        residual = RenameGuessSplitter.Normalize(residual, prePass.AlignedCurrent, desired, BuildInitializedModel, _differ);

        return (prePass, desiredModel, residual);
    }

    // Assembles the full, ordered operation list: declared renames first (so later operations see
    // the new names), then the (normalized) residual diff.
    private static List<MigrationOperation> AssembleOperations(RenamePrePass.Result prePass, IReadOnlyList<MigrationOperation> residual)
    {
        var operations = new List<MigrationOperation>(prePass.Renames.Count + residual.Count);
        operations.AddRange(prePass.Renames.Select(rename => rename.Operation));
        operations.AddRange(residual);
        return operations;
    }

    // Semantic steps (drive HasDestructiveChanges and the dry-run summary), in the same order as
    // AssembleOperations.
    private static List<MigrationStep> BuildSemanticSteps(RenamePrePass.Result prePass, IReadOnlyList<MigrationOperation> residual)
    {
        var steps = new List<MigrationStep>(prePass.Renames.Count + residual.Count);
        steps.AddRange(prePass.Renames.Select(rename => ToStep(rename.Change)));
        steps.AddRange(residual.Select(operation => ToStep(DestructiveScan.Classify(operation))));
        return steps;
    }

    // Generates the executable SQL from the WHOLE operation list in ONE call: only then does EF
    // resolve interdependent operations correctly (e.g. a SQLite table rebuild triggered by a drop
    // excludes a not-yet-added column from its INSERT ... SELECT). Generating per operation would
    // emit SQL referencing columns that do not exist at that point.
    private IReadOnlyList<string> GenerateSql(List<MigrationOperation> operations, IModel desiredModel) =>
        operations.Count == 0
            ? []
            : [.. _sqlGenerator.Generate(operations, desiredModel).Select(command => command.CommandText)];

    /// <inheritdoc/>
    public async Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        // Refused: destructive changes are never executed unless explicitly allowed. WasDryRun
        // mirrors the caller's DryRun flag here (a destructive-refused dry run is still a dry run).
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return new MigrationResult(false, plan, options.DryRun);
        }

        // Preview only: nothing is executed.
        if (options.DryRun)
        {
            return new MigrationResult(false, plan, true);
        }

        // Own connection: opened, transacted, and disposed entirely within this call, so two
        // concurrent ApplyAsync calls never race on a shared connection. The open->begin->execute
        // -each->commit sequence lives in the shared RelationalSqlBatch, which both this migrator
        // and the atomic runtime writer reuse (an uncommitted transaction rolls back on disposal).
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, ct).ConfigureAwait(false);
        }

        return new MigrationResult(true, plan, false);
    }

    // A step is purely semantic now: it names the change and whether it destroys data. The
    // executable SQL is generated once for the whole plan (see GenerateSql), not per step.
    private static MigrationStep ToStep(SchemaChange change) =>
        new(change, change.IsDestructive, change.IsDestructive ? change.Detail : null);

    private IModel BuildInitializedModel(SchemaModel schema)
    {
        // DescriptorModelBuilder.Build returns a FinalizeModel()'d model; GetRelationalModel()
        // additionally requires the runtime initializer to have run (Task 0 report, gotcha #1).
        var model = DescriptorModelBuilder.Build(schema, _newModelBuilder, _computed);
        var initialized = _modelRuntimeInitializer.Initialize(model, designTime: true);
        EnsureEveryGeneratedColumnIsExpressible(initialized);

        return initialized;
    }

    /// <summary>
    /// Refuses a <c>computed</c> field on an engine whose dialect declares it cannot express a stored generated
    /// column, naming the engine — the gate the design's D1 asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked here, after the runtime initializer, because that is the first moment the store type exists.</b>
    /// <see cref="IAlvoSqlDialect.GeneratedColumnDefinition"/> takes the column's EF-resolved store type — a
    /// dialect that needs it (PostgreSQL names the type; T-SQL rejects one being named) can only be asked once
    /// the relational model has resolved it, and a model builder runs long before that.
    /// </para>
    /// <para>
    /// <b>What is used is the answer's <em>nullness</em>, not its text, and that is deliberate rather than an
    /// oversight.</b> Measured (spike Q5–Q7): from <c>HasComputedColumnSql(…, stored: true)</c> EF's own
    /// per-provider generator already emits the full definition in <c>CREATE TABLE</c>, in PostgreSQL's in-place
    /// <c>ALTER TABLE … ADD</c>, and inside SQLite's table rebuild — so splicing the dialect's own string into
    /// the plan would be a <em>second</em> author for DDL EF has already spelled correctly, and would lose the
    /// type mapping only EF has. The member's contract is therefore read the way it is written: a
    /// <see langword="null"/> means "this engine has no stored generated column", and the migrator refuses the
    /// field rather than emitting a plain one that nothing maintains. The returned text remains the reference
    /// spelling a driver whose EF provider cannot emit the annotation would use.
    /// </para>
    /// <para>
    /// It walks the built model rather than the <see cref="SchemaModel"/> so it cannot come to disagree with
    /// what was actually configured: a field the model builder decided not to mark computed is not one this gate
    /// should have an opinion about.
    /// </para>
    /// </remarks>
    private void EnsureEveryGeneratedColumnIsExpressible(IModel model)
    {
        var generated = model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.GetComputedColumnSql() is not null);

        foreach (var property in generated)
        {
            EnsureExpressible(property);
        }
    }

    private void EnsureExpressible(IProperty property)
    {
        var column = property.GetColumnName();
        var storeType = property.GetColumnType();
        var expression = property.GetComputedColumnSql()!;

        if (_dialect.GeneratedColumnDefinition(column, storeType, expression) is null)
        {
            throw new InvalidOperationException(
                $"Field '{property.DeclaringType.DisplayName()}.{column}' declares 'computed', which Alvo "
                + $"honours as a stored generated column — and {_dialect.GetType().Name} declares that this "
                + "engine cannot express one. Remove 'computed' from the field and maintain the value in a "
                + "before-hook instead, or move the entity to an engine whose driver supports generated "
                + "columns.");
        }
    }
}
