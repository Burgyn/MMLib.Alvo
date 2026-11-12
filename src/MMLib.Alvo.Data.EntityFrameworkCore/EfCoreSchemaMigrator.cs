using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Runtime.CompilerServices;

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
public sealed partial class EfCoreSchemaMigrator : ISchemaMigrator, IUncountedSchemaPlanner
{
    private readonly IMigrationsModelDiffer _differ;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IModelRuntimeInitializer _modelRuntimeInitializer;
    private readonly Func<ModelBuilder> _newModelBuilder;
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly ComputedColumnSql? _computed;
    private readonly CheckConstraintSql? _checks;
    private readonly IFieldSqlRenderer? _fields;
    private readonly Func<EntitySchema, RollupSchema, string?> _rollupFilter;
    private readonly ILogger _logger;
    private readonly Func<string> _schemaPrefix;
    private readonly IndexFilterSql? _filters;
    private readonly ConditionalWeakTable<MigrationPlan, SchemaModel> _plannedAgainst = [];

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
    /// <param name="checks">
    /// Renders a field's <c>validation</c> to this driver's CHECK constraint SQL, or <see langword="null"/> when the
    /// container registered no expression services — a schema declaring one is then refused by name.
    /// </param>
    /// <param name="fields">
    /// This driver's field renderer, with which an apply recomputes the rollups it adds or changes
    /// (<see cref="RollupBackfill"/>), or <see langword="null"/> for a container wired without one — a plan that needs a
    /// backfill is then refused by name rather than leaving the parents stale.
    /// </param>
    /// <param name="rollupFilter">
    /// Renders a rollup's <c>where</c> over its child — the write path's own rendering — or <see langword="null"/> when the
    /// container registered no expression services, and a filtered rollup's backfill is refused by name.
    /// </param>
    /// <param name="logger">
    /// Where a violation count the database could not run is recorded for the operator, the caller seeing only the
    /// refusal; <see langword="null"/> discards it.
    /// </param>
    /// <param name="schemaPrefix">
    /// Reads the configured schema prefix, naming the sequence counter table a declared rename rewrites; <see langword="null"/>
    /// takes <see cref="AlvoOptions"/>' default. Read when a plan is built, never here: the container validates the options
    /// by resolving this migrator, so reading them while it is constructed would re-enter that resolution.
    /// </param>
    /// <param name="filters">
    /// Renders an index's <c>where</c> to this driver's partial-index predicate, or <see langword="null"/> when the container
    /// registered no expression services — a schema declaring one is then refused by name.
    /// </param>
    internal EfCoreSchemaMigrator(
        IMigrationsModelDiffer differ,
        IMigrationsSqlGenerator sqlGenerator,
        IModelRuntimeInitializer modelRuntimeInitializer,
        Func<ModelBuilder> newModelBuilder,
        RelationalConnectionFactory connections,
        IAlvoSqlDialect dialect,
        ComputedColumnSql? computed,
        CheckConstraintSql? checks,
        IFieldSqlRenderer? fields = null,
        Func<EntitySchema, RollupSchema, string?>? rollupFilter = null,
        ILogger? logger = null,
        Func<string>? schemaPrefix = null,
        IndexFilterSql? filters = null)
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
        _checks = checks;
        _fields = fields;
        _rollupFilter = rollupFilter ?? NoFilterRenderer;
        _logger = logger ?? NullLogger.Instance;
        _schemaPrefix = schemaPrefix ?? DefaultSchemaPrefix;
        _filters = filters;
    }

    private static string DefaultSchemaPrefix() => new AlvoOptions().SchemaPrefix;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Planning reads the database when the plan adds a validation to a table that already holds rows</b>: each such
    /// constraint is counted against the table (<see cref="CheckViolationScan"/>), and a positive count refuses the plan at
    /// the validation's pointer, so a dry run reports it before anything is applied (HX-11 Rulings 22, 49).
    /// </remarks>
    public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default) =>
        PlanAsync(current, desired, counted: true, ct);

    /// <inheritdoc/>
    Task<MigrationPlan> IUncountedSchemaPlanner.PlanWithoutCountingAsync(
        SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct) =>
        PlanAsync(current, desired, counted: false, ct);

    private async Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, bool counted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        ct.ThrowIfCancellationRequested();
        SequencePlan.EnsureAllocatable(desired, _dialect);
        ExclusionPlan.EnsureEnforceable(desired, _dialect, _filters);

        var (prePass, desiredModel, residual) = ComputeResidualDiff(current, desired);
        if (counted)
        {
            await RefuseRowsThatBreakNewChecksAsync(current, desired, prePass, residual, ct).ConfigureAwait(false);
            await RefuseSequenceRenameCollisionsAsync(desired, prePass, ct).ConfigureAwait(false);
        }

        var operations = ForThisEngine(AssembleOperations(prePass, residual));
        var exclusions = ExclusionPlan.Plan(prePass, desired, _dialect, _filters, TouchedTables(operations), Epoch);
        var prerequisite = await PrepareExclusionsAsync(current, prePass, exclusions.Added, counted, ct).ConfigureAwait(false);
        var backfill = Backfill(prePass.AlignedCurrent, desired);
        var locks = BackfillLocks(current, backfill);
        var steps = BuildSemanticSteps(prePass, desired, residual);
        steps.AddRange(exclusions.Changes.Select(change => ToStep(change)));
        steps.AddRange(backfill.Select(statement => BackfillStep(statement, locks)));
        var renames = SequencePlan.RenameStatements(
            prePass.Renames.Select(rename => rename.Operation), desired, SequenceTable.NameFor(_schemaPrefix()));
        var sql = GenerateSql(
            operations, desiredModel, [.. prerequisite, .. locks.Values, .. exclusions.Before],
            [.. exclusions.After, .. renames, .. backfill.Select(statement => statement.Sql)]);

        var plan = new MigrationPlan { Steps = steps, Sql = sql };
        _plannedAgainst.AddOrUpdate(plan, desired);
        return plan;
    }

    private async Task RefuseRowsThatBreakNewChecksAsync(
        SchemaModel current, SchemaModel desired, RenamePrePass.Result prePass, IReadOnlyList<MigrationOperation> residual,
        CancellationToken ct)
    {
        var targets = _checks is null
            ? []
            : CheckViolationScan.Targets(current, desired, [.. prePass.Renames.Select(rename => rename.Operation)], residual, _checks);
        if (targets.Count == 0)
        {
            return;
        }

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var errors = await BreakingRowsAsync(connection, targets, ct).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                throw new DescriptorValidationException(new DescriptorValidationResult(errors));
            }
        }
    }

    private async Task<List<DescriptorValidationError>> BreakingRowsAsync(
        DbConnection connection, IReadOnlyList<CheckViolationScan.Target> targets, CancellationToken ct)
    {
        var errors = new List<DescriptorValidationError>();
        foreach (var target in targets)
        {
            if (await BreakingRowsAsync(connection, target, ct).ConfigureAwait(false) is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    // A count the database cannot run (a drifted table missing a column `current` still names) refuses at the validation
    // it was counting for, rather than escaping as a provider exception: fail-closed, and a 422 a dry run can show (Ruling 67).
    // The provider's exception never reaches the caller, so it is logged for the operator, who may need it to tell a
    // drifted table from a transient fault (Task 10 review m4).
    private async Task<DescriptorValidationError?> BreakingRowsAsync(
        DbConnection connection, CheckViolationScan.Target target, CancellationToken ct)
    {
        try
        {
            var count = await CheckViolationScan.CountAsync(connection, target.Add.Table, target.Add.Sql, ct).ConfigureAwait(false);
            return count > 0 ? BreakingRows(target, count) : null;
        }
        catch (DbException failure)
        {
            ViolationCountFailed(_logger, target.Entity.Name, target.Field.Name, failure);
            return Uncountable(target);
        }
    }

    /// <summary>
    /// The plan's exclusion prerequisite statement, after refusing a counted plan that adds an exclusion over rows that
    /// already overlap (HX-12) and a database that cannot hold one; empty when the plan adds none.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrepareExclusionsAsync(
        SchemaModel current, RenamePrePass.Result prePass, IReadOnlyList<ExclusionPlan.Declared> added, bool counted, CancellationToken ct)
    {
        if (added.Count == 0)
        {
            return [];
        }

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            if (counted)
            {
                await RefuseOverlappingRowsAsync(connection, ExclusionOverlapScan.Countable(current, prePass, added, _filters), ct).ConfigureAwait(false);
            }

            var prerequisite = await _dialect.ExclusionPrerequisiteAsync(connection, ExclusionPlan.EqualityFields(added), ct).ConfigureAwait(false);
            return prerequisite.Refused
                ? throw Refusals(added[0].Pointer, prerequisite.Refusals)
                : prerequisite.Statement is { } statement ? [statement] : [];
        }
    }

    private static DescriptorValidationException Refusals(string exclusionPointer, IReadOnlyList<ExclusionRefusal> refusals) =>
        new(new DescriptorValidationResult(
        [
            .. refusals.Select(refusal => new DescriptorValidationError(
                refusal.Pointer ?? exclusionPointer, refusal.Message, refusal.Fix, DescriptorValidationSeverity.Error)),
        ]));

    private async Task RefuseOverlappingRowsAsync(DbConnection connection, IReadOnlyList<ExclusionPlan.Declared> countable, CancellationToken ct)
    {
        var errors = new List<DescriptorValidationError>();
        foreach (var declared in countable)
        {
            if (await OverlappingRowsAsync(connection, declared, ct).ConfigureAwait(false) is { } error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            throw new DescriptorValidationException(new DescriptorValidationResult(errors));
        }
    }

    private async Task<DescriptorValidationError?> OverlappingRowsAsync(DbConnection connection, ExclusionPlan.Declared declared, CancellationToken ct)
    {
        try
        {
            var pairs = await ExclusionOverlapScan.CountAsync(connection, declared.Definition, Epoch, ct).ConfigureAwait(false);
            return pairs > 0 ? ExclusionOverlapScan.Overlapping(declared, pairs) : null;
        }
        catch (DbException failure)
        {
            OverlapCountFailed(_logger, declared.Entity.Name, failure);
            return ExclusionOverlapScan.Uncountable(declared);
        }
    }

    /// <summary>
    /// Refuses a declared rename onto a name that still holds an earlier sequence's counter rows (they are never deleted),
    /// before the apply's key rewrite would hit the counter table's primary key mid-transaction.
    /// </summary>
    private async Task RefuseSequenceRenameCollisionsAsync(SchemaModel desired, RenamePrePass.Result prePass, CancellationToken ct)
    {
        var collisions = SequencePlan.Collisions(
            prePass.Renames.Select(rename => rename.Operation), desired, SequenceTable.NameFor(_schemaPrefix()));
        if (collisions.Count == 0)
        {
            return;
        }

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var errors = new List<DescriptorValidationError>();
            foreach (var collision in collisions)
            {
                if (await CountersUnderAsync(connection, collision.CountSql, ct).ConfigureAwait(false) > 0)
                {
                    errors.Add(collision.Error);
                }
            }

            if (errors.Count > 0)
            {
                throw new DescriptorValidationException(new DescriptorValidationResult(errors));
            }
        }
    }

    /// <summary>The counter rows a collision probe finds; none when no create has made the counter table yet.</summary>
    private static async Task<long> CountersUnderAsync(DbConnection connection, string countSql, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = countSql;
            try
            {
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (DbException)
            {
                return 0;
            }
        }
    }

    private static DescriptorValidationError Uncountable(CheckViolationScan.Target target) => new(
        $"/entities/{target.Entity.Name}/fields/{target.Field.Name}/validation",
        $"Could not count the rows of '{target.Entity.Name}' that break this validation, so the CHECK constraint cannot be added. Nothing was applied.",
        "Check that the database still matches the applied schema — a column this validation reads may be missing — then apply again.",
        DescriptorValidationSeverity.Error);

    private static DescriptorValidationError BreakingRows(CheckViolationScan.Target target, long count) => new(
        $"/entities/{target.Entity.Name}/fields/{target.Field.Name}/validation",
        $"{count} row(s) of '{target.Entity.Name}' break this validation, so the CHECK constraint cannot be added. Nothing was applied.",
        "Correct those rows first — or relax the expression — then apply again.",
        DescriptorValidationSeverity.Error);

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

    // Assembles the full, ordered operation list: the drops of the partial indexes a rename released (by their old names,
    // which no rename changes), then declared renames (so later operations see the new names), then the drops of the CHECK constraints a rename released (see RenamePrePass), then the
    // (normalized) residual diff, which adds them back under their new names.
    private static List<MigrationOperation> AssembleOperations(RenamePrePass.Result prePass, IReadOnlyList<MigrationOperation> residual)
    {
        var operations = new List<MigrationOperation>(
            prePass.ReleasedIndexes.Count + prePass.Renames.Count + prePass.ReleasedChecks.Count + residual.Count);
        operations.AddRange(prePass.ReleasedIndexes.Select(released => released.Operation));
        operations.AddRange(prePass.Renames.Select(rename => rename.Operation));
        operations.AddRange(prePass.ReleasedChecks.Select(released => released.Operation));
        operations.AddRange(residual);
        return operations;
    }

    // The operations as this engine can execute them: a stored generated column added to a table SQLite may
    // already hold rows in becomes the two hops EF answers with a rebuild (see GeneratedColumnAdds). Steps are
    // built from the differ's own operations, so this changes the SQL and never the plan's classification.
    private List<MigrationOperation> ForThisEngine(List<MigrationOperation> operations) =>
        _dialect.GeneratedColumnAddRequiresTableRebuild ? GeneratedColumnAdds.AsRebuilds(operations) : operations;

    // Semantic steps (drive HasDestructiveChanges and the dry-run summary), in the same order as
    // AssembleOperations.
    private List<MigrationStep> BuildSemanticSteps(
        RenamePrePass.Result prePass, SchemaModel desired, IReadOnlyList<MigrationOperation> residual)
    {
        var steps = new List<MigrationStep>(
            prePass.ReleasedIndexes.Count + prePass.Renames.Count + prePass.ReleasedChecks.Count + residual.Count);
        steps.AddRange(prePass.ReleasedIndexes.Select(released => ToStep(released.Change)));
        steps.AddRange(prePass.Renames.Select(rename => ToStep(rename.Change)));
        steps.AddRange(prePass.ReleasedChecks.Select(released => ToStep(released.Change, CostNote(released.Operation))));
        steps.AddRange(residual.Select(operation =>
            ToStep(WithValidatedField(DestructiveScan.Classify(operation), operation, prePass.AlignedCurrent, desired), CostNote(operation))));
        return steps;
    }

    // A CHECK constraint's step names the field whose validation it is: recomputed from the constraint's name over the
    // schema that holds it — the desired one for an added constraint, the (aligned) current one for a dropped one —
    // because a constraint name is never parsed (HX-11 Ruling 19).
    private static SchemaChange WithValidatedField(
        SchemaChange change, MigrationOperation operation, SchemaModel current, SchemaModel desired) =>
        operation switch
        {
            AddCheckConstraintOperation add => change with { Field = ValidatedField(desired, add.Table, add.Name) },
            DropCheckConstraintOperation drop => change with { Field = ValidatedField(current, drop.Table, drop.Name) },
            _ => change,
        };

    private static string? ValidatedField(SchemaModel schema, string table, string constraintName) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, table, StringComparison.Ordinal)) is { } entity
            ? CheckConstraintNames.Owner(schema, entity, constraintName)?.Field.Name
            : null;

    // What a non-destructive step costs when that is worth saying before it runs: on an engine that rebuilds the table
    // to add a generated column or to add or drop a CHECK constraint, the step copies every row under a write lock — a
    // pause on a large table, not data loss, so it is a note on the step and never the destructive flag.
    private string? CostNote(MigrationOperation operation) =>
        _dialect.GeneratedColumnAddRequiresTableRebuild && RebuildsTheTable(operation)
            ? GeneratedColumnAdds.RebuildNote
            : null;

    private static bool RebuildsTheTable(MigrationOperation operation) =>
        GeneratedColumnAdds.IsGeneratedAdd(operation) || operation is AddCheckConstraintOperation or DropCheckConstraintOperation;

    // Generates the executable SQL from the WHOLE operation list in ONE call: only then does EF
    // resolve interdependent operations correctly (e.g. a SQLite table rebuild triggered by a drop
    // excludes a not-yet-added column from its INSERT ... SELECT). Generating per operation would
    // emit SQL referencing columns that do not exist at that point.
    // The dialect's lock-timeout statement leads, so it is the first statement of the apply's transaction on either executor
    // and a dry run shows it (HX-11 Ruling 20). The backfill's child locks come next, before the DDL — so the apply takes
    // each child before its parent, a writer's own order (Ruling 74) — and the rollup backfill follows EF's DDL, so it reads
    // and writes the columns the DDL has just made (Ruling 32). An exclusion's prerequisite (PostgreSQL's btree_gist) leads
    // the locks; its drops precede the DDL — and so the renames — and its creates follow it, so an EF table rebuild never
    // runs after a trigger it would take with the old table (HX-12).
    private IReadOnlyList<string> GenerateSql(
        List<MigrationOperation> operations, IModel desiredModel, IReadOnlyList<string> beforeDdl, IReadOnlyList<string> afterDdl)
    {
        if (operations.Count == 0 && beforeDdl.Count == 0 && afterDdl.Count == 0)
        {
            return [];
        }

        var ddl = operations.Count == 0
            ? []
            : _sqlGenerator.Generate(operations, desiredModel).Select(command => command.CommandText);
        return [.. LockTimeout, .. beforeDdl, .. ddl, .. afterDdl];
    }

    /// <summary>Every table an operation of the plan names — the set an engine that rebuilds tables may rebuild.</summary>
    private static HashSet<string> TouchedTables(IEnumerable<MigrationOperation> operations) =>
    [
        .. operations.SelectMany(operation => operation switch
        {
            RenameTableOperation rename => [rename.Name, rename.NewName ?? rename.Name],
            TableOperation table => [table.Name],
            ITableMigrationOperation table => [table.Table],
            _ => Array.Empty<string>(),
        }),
    ];

    private string Epoch(string column) =>
        _fields?.RenderEpochMicroseconds(column) ?? throw new InvalidOperationException(
            "This plan checks a datetime exclusion over the rows a table already holds, which needs the driver's field "
            + "renderer — and this migrator was built without one. Register the driver through its UseSqlite/UsePostgreSql entry point.");

    /// <summary>
    /// The rollup recomputes this plan needs (HX-11 Ruling 32), over the current schema as the declared renames leave it —
    /// so a renamed parent or field is compared under its new name.
    /// </summary>
    private IReadOnlyList<RollupBackfill.Statement> Backfill(SchemaModel alignedCurrent, SchemaModel desired)
    {
        if (RollupBackfill.Targets(alignedCurrent, desired).Count == 0)
        {
            return [];
        }

        var fields = _fields ?? throw new InvalidOperationException(
            "This plan adds or changes a rollup on a table that already holds rows, which the apply recomputes — and this "
            + "migrator was built without the driver's field renderer to write that recompute with. Register the driver "
            + "through its UseSqlite/UsePostgreSql entry point, which supplies it.");
        return RollupBackfill.Statements(alignedCurrent, desired, _dialect, fields, _rollupFilter);
    }

    /// <summary>
    /// The child lock of every distinct child the backfill reads that already exists in <paramref name="current"/>, keyed by
    /// name in ordinal order (Ruling 74): one fixed order across applies, and none for a child the plan creates — it has
    /// no table before the DDL, and no writer to hold off. An engine whose dialect answers no lock contributes nothing.
    /// </summary>
    private SortedDictionary<string, string> BackfillLocks(SchemaModel current, IReadOnlyList<RollupBackfill.Statement> backfill)
    {
        var locks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in backfill.Select(statement => statement.Child))
        {
            if (!locks.ContainsKey(child.Name)
                && current.Entities.Any(entity => string.Equals(entity.Name, child.Name, StringComparison.Ordinal))
                && _dialect.BackfillLockStatement(child) is { } statement)
            {
                locks.Add(child.Name, statement);
            }
        }

        return locks;
    }

    private static MigrationStep BackfillStep(RollupBackfill.Statement statement, SortedDictionary<string, string> locks) => ToStep(
        new SchemaChange
        {
            Kind = SchemaChangeKind.AlterField,
            Entity = statement.Entity,
            Field = statement.Field,
            Detail = "Recomputes the rollup for every record.",
        },
        locks.ContainsKey(statement.Child.Name)
            ? $"Locks writes to '{statement.Child.Name}' and every changed parent row until the apply commits."
            : null);

    private static string? NoFilterRenderer(EntitySchema child, RollupSchema rollup) =>
        rollup.Where is null
            ? null
            : throw new InvalidOperationException(
                $"A rollup over '{child.Name}' declares a 'where' filter, which the apply's recompute renders with the "
                + "expression services — and this container registered none. Call AddAlvo() on the host's builder.");

    private IEnumerable<string> LockTimeout =>
        _dialect.ApplyLockTimeoutStatement is { } statement ? [statement] : [];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>A <c>CHECK</c> the engine refuses during the apply</b> — rows written after the plan's count, or a recomputed
    /// rollup breaking a parent's validation — is a <see cref="DescriptorValidationException"/> at the owning validation's
    /// pointer, and nothing is applied (HX-11 Ruling 22).
    /// </para>
    /// <para>
    /// <b>A lock timeout escapes as an internal exception, deliberately.</b> On an engine whose dialect bounds the apply's
    /// lock wait (PostgreSQL, five seconds), giving up raises <c>AlvoApplyLockTimeoutException</c>, an internal type: the
    /// family's own surfaces answer it — the Management API with a <c>409</c> and <c>Retry-After</c>, the boot with a
    /// refused start — and an embedded host calling this member directly receives it as an exception it cannot catch by
    /// name. Nothing was applied; the same plan may be retried. Publishing the type would make one apply's retry signal a
    /// contract no host has asked for (HX-11 Ruling 20, Task 9 review m3).
    /// </para>
    /// </remarks>
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
        await ExecuteTranslatedAsync(plan, ct).ConfigureAwait(false);
        return new MigrationResult(true, plan, false);
    }

    /// <summary>
    /// Runs the plan on its own connection, answering a refusal the engine raised the way the runtime writer does
    /// (<see cref="CheckApplyRefusal"/>), with the pointer resolved from the schema the plan was made for.
    /// </summary>
    /// <remarks>
    /// A plan this migrator did not make — or a copy of one — carries no schema, so a refused validation is then reported
    /// at the document's root rather than guessed at.
    /// </remarks>
    private async Task ExecuteTranslatedAsync(MigrationPlan plan, CancellationToken ct)
    {
        try
        {
            var connection = _connections.Create();
            await using (connection.ConfigureAwait(false))
            {
                await ExecuteFramedAsync(connection, plan, ct).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (CheckApplyRefusal.Translate(failure, _dialect, PlannedAgainst(plan), plan.Sql) is { } refused)
        {
            throw refused;
        }
    }

    private SchemaModel PlannedAgainst(MigrationPlan plan) =>
        _plannedAgainst.TryGetValue(plan, out var desired) ? desired : new SchemaModel([]);

    /// <summary>
    /// Runs the plan's SQL in one transaction, framed by the statements this engine only honours
    /// <b>outside</b> one — see <see cref="MigrationFramingScope"/>, which the atomic runtime writer shares.
    /// </summary>
    private Task<bool> ExecuteFramedAsync(DbConnection connection, MigrationPlan plan, CancellationToken ct) =>
        MigrationFramingScope.RunAsync(
            connection,
            _dialect.MigrationFraming,
            () => ExecuteVerifiedAsync(connection, plan, ct),
            ct);

    /// <summary>The plan's SQL in one transaction, verified by the dialect's framing before it commits.</summary>
    private async Task<bool> ExecuteVerifiedAsync(DbConnection connection, MigrationPlan plan, CancellationToken ct)
    {
        await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, transaction, ct).ConfigureAwait(false);
            await MigrationFramingScope.VerifyAsync(connection, transaction, _dialect.MigrationFraming, plan, ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    // A step is purely semantic now: it names the change and whether it destroys data. The
    // executable SQL is generated once for the whole plan (see GenerateSql), not per step.
    private static MigrationStep ToStep(SchemaChange change, string? costNote = null) =>
        new(change, change.IsDestructive, change.IsDestructive ? change.Detail : costNote);

    private IModel BuildInitializedModel(SchemaModel schema)
    {
        // DescriptorModelBuilder.Build returns a FinalizeModel()'d model; GetRelationalModel()
        // additionally requires the runtime initializer to have run (Task 0 report, gotcha #1).
        CheckConstraintNames.EnsureDistinct(schema);
        var model = DescriptorModelBuilder.Build(schema, _newModelBuilder, _computed, _checks, _filters);
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not count the rows of '{Entity}' that break the validation of '{Field}'; the plan was refused at that validation.")]
    private static partial void ViolationCountFailed(ILogger logger, string entity, string field, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not count the overlapping rows of '{Entity}' an exclusion forbids; the plan was refused at that exclusion.")]
    private static partial void OverlapCountFailed(ILogger logger, string entity, Exception exception);
}
