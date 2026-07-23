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
/// is needed here.
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

        // 1. Pre-apply the descriptor's renames to the current schema so the differ sees the
        //    renamed members already aligned by name (no drop+add), then diff.
        var prePass = RenamePrePass.Compute(current, desired);
        var currentModel = BuildInitializedModel(prePass.AlignedCurrent);
        var desiredModel = BuildInitializedModel(desired);

        var residual = _differ.GetDifferences(currentModel.GetRelationalModel(), desiredModel.GetRelationalModel());

        // 2. Renames first (so later operations see the new names), then the residual diff.
        var steps = new List<MigrationStep>(prePass.Renames.Count + residual.Count);
        foreach (var rename in prePass.Renames)
        {
            steps.Add(ToStep(rename.Operation, rename.Change, desiredModel));
        }

        foreach (var operation in residual)
        {
            steps.Add(ToStep(operation, DestructiveScan.Classify(operation), desiredModel));
        }

        return Task.FromResult(new MigrationPlan { Steps = steps });
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
                foreach (var step in plan.Steps)
                {
                    if (string.IsNullOrWhiteSpace(step.Sql))
                    {
                        continue;
                    }

                    var command = _connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = step.Sql;
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

    private MigrationStep ToStep(MigrationOperation operation, SchemaChange change, IModel desiredModel)
    {
        // Generate SQL per operation so each step's Sql maps exactly to its originating change.
        // The DESIRED model is passed to Generate: a SQLite table rebuild rebuilds to the target
        // shape (see Task 0 report), so the target model is load-bearing here.
        var commands = _sqlGenerator.Generate([operation], desiredModel);
        var sql = string.Join(Environment.NewLine, commands.Select(c => c.CommandText));
        return new MigrationStep(change, sql, change.IsDestructive, change.IsDestructive ? change.Detail : null);
    }

    private IModel BuildInitializedModel(SchemaModel schema)
    {
        // DescriptorModelBuilder.Build returns a FinalizeModel()'d model; GetRelationalModel()
        // additionally requires the runtime initializer to have run (Task 0 report, gotcha #1).
        var model = DescriptorModelBuilder.Build(schema, _newModelBuilder);
        return _modelRuntimeInitializer.Initialize(model, designTime: true);
    }

    /// <summary>
    /// Disposes the internal serialization gate. The provider-supplied <see cref="DbConnection"/>
    /// is not owned by this type (see the field's TODO) and is left untouched.
    /// </summary>
    public void Dispose() => _gate.Dispose();
}
