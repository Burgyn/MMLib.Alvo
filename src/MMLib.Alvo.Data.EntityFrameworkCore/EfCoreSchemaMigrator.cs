using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// An <see cref="ISchemaMigrator"/> that reuses EF Core's migrations differ and per-provider SQL
/// generator to turn a (current, desired) pair of <see cref="SchemaModel"/>s into a
/// <see cref="MigrationPlan"/>.
/// </summary>
/// <remarks>
/// The provider-specific services (<see cref="IMigrationsModelDiffer"/>,
/// <see cref="IMigrationsSqlGenerator"/>, <see cref="IModelRuntimeInitializer"/>) and the
/// conventionless <see cref="ModelBuilder"/> factory are injected by the provider wiring
/// (SQLite/PostgreSQL packages), so this type only depends on EFCore.Relational abstractions and
/// stays provider-agnostic.
/// </remarks>
internal sealed class EfCoreSchemaMigrator : ISchemaMigrator
{
    private readonly IMigrationsModelDiffer _differ;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IModelRuntimeInitializer _modelRuntimeInitializer;
    private readonly Func<ModelBuilder> _newModelBuilder;

    public EfCoreSchemaMigrator(
        IMigrationsModelDiffer differ,
        IMigrationsSqlGenerator sqlGenerator,
        IModelRuntimeInitializer modelRuntimeInitializer,
        Func<ModelBuilder> newModelBuilder)
    {
        ArgumentNullException.ThrowIfNull(differ);
        ArgumentNullException.ThrowIfNull(sqlGenerator);
        ArgumentNullException.ThrowIfNull(modelRuntimeInitializer);
        ArgumentNullException.ThrowIfNull(newModelBuilder);

        _differ = differ;
        _sqlGenerator = sqlGenerator;
        _modelRuntimeInitializer = modelRuntimeInitializer;
        _newModelBuilder = newModelBuilder;
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
    public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default) =>
        throw new NotImplementedException("Applying a migration plan is implemented in Task 10.");

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
}
