using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A deadlock the engine broke by failing this write is answered as the port's retryable
/// <see cref="AlvoTransientConflictException"/>, and nothing else is: whether a failure is one is the dialect's decision, and
/// a failure it does not claim propagates as the very exception the write raised (issue 443).
/// </summary>
public class TransientConflictTranslatorTests
{
    private static readonly DeadlockingDialect _dialect = new();

    [Fact]
    public async Task A_deadlock_is_a_transient_conflict_carrying_the_engines_failure()
    {
        var deadlock = new EngineFailure(Deadlock);

        var refused = await Should.ThrowAsync<AlvoTransientConflictException>(
            () => TransientConflictTranslator.MappedAsync<int>(() => throw deadlock, _dialect));

        refused.InnerException.ShouldBeSameAs(deadlock);
    }

    [Fact]
    public async Task A_deadlock_inside_efs_update_wrapper_is_a_transient_conflict_too()
    {
        var deadlock = new EngineFailure(Deadlock);

        var refused = await Should.ThrowAsync<AlvoTransientConflictException>(
            () => TransientConflictTranslator.MappedAsync(() => throw new DbUpdateException("save failed", deadlock), _dialect));

        refused.InnerException.ShouldBeSameAs(deadlock);
    }

    [Fact]
    public async Task Any_other_engine_failure_propagates_untouched()
    {
        var other = new EngineFailure("constraint");

        var thrown = await Should.ThrowAsync<EngineFailure>(
            () => TransientConflictTranslator.MappedAsync<int>(() => throw other, _dialect));

        thrown.ShouldBeSameAs(other);
    }

    /// <summary>
    /// The Npgsql provider's execution strategy wraps a failure it deems transient — a deadlock is one — in an
    /// <see cref="InvalidOperationException"/>; measured by the PostgreSQL fact, which saw exactly that shape.
    /// </summary>
    [Fact]
    public async Task A_deadlock_behind_the_providers_transient_failure_wrapper_is_a_transient_conflict()
    {
        var deadlock = new EngineFailure(Deadlock);

        var refused = await Should.ThrowAsync<AlvoTransientConflictException>(
            () => TransientConflictTranslator.MappedAsync<int>(
                () => throw new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", deadlock),
                _dialect));

        refused.InnerException.ShouldBeSameAs(deadlock);
    }

    [Fact]
    public async Task A_wrapper_around_no_engine_failure_propagates_untouched()
    {
        var invariant = new InvalidOperationException("broken", new ArgumentException("inner"));

        (await Should.ThrowAsync<InvalidOperationException>(
            () => TransientConflictTranslator.MappedAsync<int>(() => throw invariant, _dialect))).ShouldBeSameAs(invariant);
    }

    /// <summary>A call nested in a call is mapped where it failed; the outer boundary passes that refusal on, not a copy of it.</summary>
    [Fact]
    public async Task A_conflict_already_answered_passes_through_as_itself()
    {
        var answered = new AlvoTransientConflictException(new EngineFailure(Deadlock));

        var thrown = await Should.ThrowAsync<AlvoTransientConflictException>(
            () => TransientConflictTranslator.MappedAsync<int>(() => throw answered, _dialect));

        thrown.ShouldBeSameAs(answered);
    }

    [Fact]
    public async Task A_write_that_succeeds_answers_its_own_result()
        => (await TransientConflictTranslator.MappedAsync(() => Task.FromResult(42), _dialect)).ShouldBe(42);

    [Fact]
    public async Task Without_a_dialect_nothing_runs()
        => (await Should.ThrowAsync<ArgumentNullException>(
            () => TransientConflictTranslator.MappedAsync(() => Task.FromResult(1), null!))).ParamName.ShouldBe("dialect");

    private const string Deadlock = "deadlock";

    private sealed class EngineFailure(string state) : DbException(state)
    {
        public string State { get; } = state;
    }

    /// <summary>A dialect that calls a failure a deadlock when its scripted state says so — the engine's half, scripted.</summary>
    private sealed class DeadlockingDialect : IAlvoSqlDialect
    {
        public string RowLockClause(PreImageMutation mutation) => string.Empty;

        public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) => AlvoSqlIdentifier.Quote(entity!.Name);

        public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

        public string RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})";

        public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) => null;

        bool IAlvoSqlDialect.IsDeadlock(DbException failure) => failure is EngineFailure { State: Deadlock };
    }
}
