using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;
using Shouldly;
using Xunit;

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

    /// <summary>
    /// <b>The default of <see cref="IAlvoSqlDialect.GeneratedColumnDefinition"/> is a refusal, not a
    /// spelling.</b> <see cref="TestSqlDialect"/> implements the port's required members and nothing else, so
    /// it is the one implementation in the repo that can prove what an out-of-repo driver inherits. A default
    /// that guessed <c>GENERATED ALWAYS AS (…) STORED</c> would compile and migrate on two engines and produce
    /// a syntax error on the third, or — on an engine where the phrase happens to parse differently — an
    /// ordinary column nothing ever maintains, which is the silent outcome <c>computed</c> was refused for in
    /// the first place.
    /// </summary>
    [Fact]
    public void An_implementor_that_adds_nothing_cannot_express_a_generated_column()
    {
        var dialect = CreateDialect();

        dialect.GeneratedColumnDefinition("line_total", "numeric(18,2)", "(1 + 1)").ShouldBeNull();
    }
}
