using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// An <see cref="ISchemaMigrator"/> that reuses EF Core's migrations differ and per-provider SQL
/// generator to turn a (current, desired) pair of <see cref="SchemaModel"/>s into a
/// <see cref="MigrationPlan"/>, and executes the resulting plan over a provider-supplied ADO.NET
/// connection.
/// </summary>
/// <remarks>
/// The provider-specific services (<see cref="IMigrationsModelDiffer"/>,
/// <see cref="IMigrationsSqlGenerator"/>, <see cref="IModelRuntimeInitializer"/>) and the
/// conventionless <see cref="ModelBuilder"/> factory are injected by the provider wiring
/// (SQLite/PostgreSQL packages), so this type only depends on EFCore.Relational abstractions and
/// stays provider-agnostic. The <see cref="DbConnection"/> is likewise provider-supplied: plain
/// ADO.NET is enough to execute the already-generated SQL, so no relational-command infrastructure
/// is needed here. The provider constructs that connection solely to hand it to this instance, so
/// this type owns it and disposes it in <see cref="Dispose"/>.
///
/// <para>
/// <see cref="ApplyAsync"/> serializes its connection-touching work within one instance (an
/// internal gate around open/transaction/execute), so two concurrent callers sharing the same
/// migrator instance never race on its single <see cref="DbConnection"/>. This makes the type
/// safe under a long-lived (e.g. singleton) registration, but it is still intended for
/// controlled use — startup migrations or a single orchestrator (CLI/dashboard) — not as the
/// concurrency-control mechanism for many independent clients changing the schema at runtime;
/// that is governed by descriptor optimistic locking (PR-B), not by this type.
/// </para>
/// </remarks>
internal sealed class EfCoreSchemaMigrator : ISchemaMigrator, IDisposable
{
    private readonly IMigrationsModelDiffer _differ;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IModelRuntimeInitializer _modelRuntimeInitializer;
    private readonly Func<ModelBuilder> _newModelBuilder;

    // TODO(PR-B): replace this single shared connection with a per-call connection (factory)
    // once runtime concurrent schema changes need real parallelism instead of serialization.
    // Owned by this instance (the provider constructs it solely to hand it to us) — disposed
    // alongside the gate; see Dispose().
    private readonly DbConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EfCoreSchemaMigrator(
        IMigrationsModelDiffer differ,
        IMigrationsSqlGenerator sqlGenerator,
        IModelRuntimeInitializer modelRuntimeInitializer,
        Func<ModelBuilder> newModelBuilder,
        DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(differ);
        ArgumentNullException.ThrowIfNull(sqlGenerator);
        ArgumentNullException.ThrowIfNull(modelRuntimeInitializer);
        ArgumentNullException.ThrowIfNull(newModelBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        _differ = differ;
        _sqlGenerator = sqlGenerator;
        _modelRuntimeInitializer = modelRuntimeInitializer;
        _newModelBuilder = newModelBuilder;
        _connection = connection;
    }

    /// <inheritdoc/>
    public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        ct.ThrowIfCancellationRequested();

        // 1. Pre-apply the descriptor's DECLARED renames to the current schema so the differ sees
        //    the renamed members already aligned by name (no drop+add), then diff.
        var prePass = RenamePrePass.Compute(current, desired);
        var currentModel = BuildInitializedModel(prePass.AlignedCurrent);
        var desiredModel = BuildInitializedModel(desired);

        var residual = _differ.GetDifferences(currentModel.GetRelationalModel(), desiredModel.GetRelationalModel());

        // 2. Neutralize EF's GUESSED renames: any rename op left in the residual (i.e. not one of
        //    our declared, pre-aligned renames) is a heuristic pairing of an unrelated drop+add and
        //    must be split back into its destructive Drop + fresh Add, or it would bypass the
        //    destructive guardrail and silently carry data into a semantically-unrelated column.
        residual = RenameGuessSplitter.Normalize(residual, prePass.AlignedCurrent, desired, BuildInitializedModel, _differ);

        // 3. Assemble the full, ordered operation list: declared renames first (so later operations
        //    see the new names), then the (normalized) residual diff.
        var operations = new List<MigrationOperation>(prePass.Renames.Count + residual.Count);
        operations.AddRange(prePass.Renames.Select(rename => rename.Operation));
        operations.AddRange(residual);

        // 4. Semantic steps (drive HasDestructiveChanges and the dry-run summary), in the same order.
        var steps = new List<MigrationStep>(operations.Count);
        steps.AddRange(prePass.Renames.Select(rename => ToStep(rename.Change)));
        steps.AddRange(residual.Select(operation => ToStep(DestructiveScan.Classify(operation))));

        // 5. Generate the executable SQL from the WHOLE operation list in ONE call: only then does
        //    EF resolve interdependent operations correctly (e.g. a SQLite table rebuild triggered
        //    by a drop excludes a not-yet-added column from its INSERT ... SELECT). Generating per
        //    operation would emit SQL referencing columns that do not exist at that point.
        IReadOnlyList<string> sql = operations.Count == 0
            ? []
            : [.. _sqlGenerator.Generate(operations, desiredModel).Select(command => command.CommandText)];

        return Task.FromResult(new MigrationPlan { Steps = steps, Sql = sql });
    }

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

        // Serialize connection-touching work: this instance's single DbConnection cannot safely
        // run two open/transaction/execute sequences at once (see the class remarks).
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct).ConfigureAwait(false);
            }

            var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var commandText in plan.Sql)
                {
                    if (string.IsNullOrWhiteSpace(commandText))
                    {
                        continue;
                    }

                    var command = _connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = commandText;
                        command.Transaction = transaction;
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new MigrationResult(true, plan, false);
    }

    // A step is purely semantic now: it names the change and whether it destroys data. The
    // executable SQL is generated once for the whole plan (see PlanAsync step 5), not per step.
    private static MigrationStep ToStep(SchemaChange change) =>
        new(change, change.IsDestructive, change.IsDestructive ? change.Detail : null);

    private IModel BuildInitializedModel(SchemaModel schema)
    {
        // DescriptorModelBuilder.Build returns a FinalizeModel()'d model; GetRelationalModel()
        // additionally requires the runtime initializer to have run (Task 0 report, gotcha #1).
        var model = DescriptorModelBuilder.Build(schema, _newModelBuilder);
        return _modelRuntimeInitializer.Initialize(model, designTime: true);
    }

    /// <summary>
    /// Disposes the internal serialization gate and the provider-supplied <see cref="DbConnection"/>
    /// this instance owns, releasing (e.g.) the underlying file handle deterministically instead of
    /// relying on process exit or connection-pool finalization.
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
        _connection.Dispose();
    }
}
