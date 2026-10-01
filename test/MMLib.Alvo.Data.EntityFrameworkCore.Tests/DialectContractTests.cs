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

/// <summary>
/// A leg whose field renderer implements only the port's required members, so it inherits
/// <see cref="IFieldSqlRenderer"/>'s deny-by-default for text: <see cref="IFieldSqlRenderer.RenderStringLiteral"/>
/// answers <see langword="null"/> and <see cref="IFieldSqlRenderer.RenderStringConcatenation"/> throws
/// <see cref="NotSupportedException"/>. It proves the public contract suite reads both as a decline, so an
/// out-of-repo dialect that never opts into text can still inherit it and pass.
/// </summary>
public class DecliningTextSqlDialectContractTests : AlvoSqlDialectContractTests
{
    protected override IAlvoSqlDialect CreateDialect() => new TestSqlDialect();

    protected override IFieldSqlRenderer CreateFieldRenderer() => new RequiredMembersOnly();

    [Fact]
    public void The_renderer_under_test_really_declines_text()
    {
        var fields = CreateFieldRenderer();

        fields.RenderStringLiteral("a").ShouldBeNull();
        Should.Throw<NotSupportedException>(() => fields.RenderStringConcatenation("a", "b"));
    }

    /// <summary><see cref="TestFieldSqlRenderer"/>'s required members, and none of the defaulted ones.</summary>
    private sealed class RequiredMembersOnly : IFieldSqlRenderer
    {
        private readonly TestFieldSqlRenderer _inner = new();

        public string TrueLiteral => _inner.TrueLiteral;

        public string FalseLiteral => _inner.FalseLiteral;

        public string RenderField(MMLib.Alvo.Schema.EntitySchema entity, string fieldName) => _inner.RenderField(entity, fieldName);

        public string RenderParameter(string parameterName) => _inner.RenderParameter(parameterName);

        public string RenderCaseInsensitiveLike(string left, string right) => _inner.RenderCaseInsensitiveLike(left, right);
    }
}
