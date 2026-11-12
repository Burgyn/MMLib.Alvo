using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// An apply the database failed to break a deadlock — a parent's cascade delete against the backfill's child locks, the
/// residual HX-11 Ruling 74 names — is the apply's retryable lock refusal, whichever executor ran it, and says it was a
/// deadlock (issue 443).
/// </summary>
public class CheckApplyRefusalDeadlockTests
{
    private static readonly SchemaModel _desired = new([AlvoDataFixtures.Vehicle]);

    [Fact]
    public void A_deadlock_is_the_retryable_lock_refusal_naming_the_deadlock_and_keeping_the_engines_failure()
    {
        var deadlock = new EngineFailure(Deadlock);

        var refused = CheckApplyRefusal.Translate(deadlock, new ScriptedDialect(), _desired).ShouldBeOfType<AlvoApplyLockTimeoutException>();

        refused.Message.ShouldBe(AlvoApplyLockTimeoutException.DeadlockRefusal);
        refused.InnerException.ShouldBeSameAs(deadlock);
        refused.AtTheVersionRow.ShouldBeFalse();
    }

    [Fact]
    public void A_lock_timeout_still_says_lock_timeout()
        => CheckApplyRefusal.Translate(new EngineFailure(LockTimeout), new ScriptedDialect(), _desired)
            .ShouldBeOfType<AlvoApplyLockTimeoutException>().Message.ShouldBe(AlvoApplyLockTimeoutException.Refusal);

    [Fact]
    public void Any_other_engine_failure_is_no_refusal()
        => CheckApplyRefusal.Translate(new EngineFailure("other"), new ScriptedDialect(), _desired).ShouldBeNull();

    private const string Deadlock = "deadlock";

    private const string LockTimeout = "lock-timeout";

    private sealed class EngineFailure(string state) : DbException(state)
    {
        public string State { get; } = state;
    }

    private sealed class ScriptedDialect : IAlvoSqlDialect
    {
        public string RowLockClause(PreImageMutation mutation) => string.Empty;

        public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) => AlvoSqlIdentifier.Quote(entity!.Name);

        public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

        public string RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})";

        public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) => null;

        bool IAlvoSqlDialect.IsDeadlock(DbException failure) => failure is EngineFailure { State: Deadlock };

        bool IAlvoSqlDialect.IsLockTimeout(DbException failure) => failure is EngineFailure { State: LockTimeout };
    }
}
