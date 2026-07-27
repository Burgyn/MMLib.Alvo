using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The T-SQL fake's leg of the SQL-seam contract — and the only leg that exercises the <em>hinted</em> arm of
/// the pairing rule, because both shipped drivers express row locking in the trailing position or not at all.
/// Without it, "a lock is expressed in one position, never both" would be a fact no implementation ever tested
/// positively.
/// </summary>
public class TSqlSqlDialectContractTests : AlvoSqlDialectContractTests
{
    protected override IAlvoSqlDialect CreateDialect() => new TSqlSqlDialect();

    protected override IFieldSqlRenderer CreateFieldRenderer() => new TSqlFieldSqlRenderer();
}

/// <summary>
/// The composer's own stand-in dialect, held to the same contract. It exists to let the projection and
/// statement-composer tests assert composed text without an engine, so a grammar it got wrong would make those
/// snapshots prove the wrong thing — the fake has to be a legal dialect for the statements built on it to mean
/// anything.
/// </summary>
public class TestSqlDialectContractTests : AlvoSqlDialectContractTests
{
    protected override IAlvoSqlDialect CreateDialect() => new TestSqlDialect();

    protected override IFieldSqlRenderer CreateFieldRenderer() => new TestFieldSqlRenderer();
}
