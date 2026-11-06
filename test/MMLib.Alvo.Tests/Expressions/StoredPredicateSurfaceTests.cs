using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class StoredPredicateSurfaceTests
{
    [Theory]
    [InlineData("owner_id == @user.id", "@user")]
    [InlineData("tenant_id == @tenant.id", "@tenant")]
    [InlineData("'admin' in @user.roles", "role")]
    [InlineData("created_at <= now()", "now()")]
    public void A_stored_predicate_reads_no_caller_and_no_clock(string rule, string named) =>
        StoredPredicateSurface.Refusals(CelFixtures.CompileRule(rule), "A validation").ShouldHaveSingleItem()
            .Message.ShouldSatisfyAllConditions(m => m.ShouldContain(named), m => m.ShouldStartWith("A validation"));

    [Theory]
    [InlineData("total >= 0")]
    [InlineData("approved_at - created_at <= duration('8h') && title != 'void'")]
    public void A_predicate_over_the_row_alone_passes(string rule) =>
        StoredPredicateSurface.Refusals(CelFixtures.CompileRule(rule), "A validation").ShouldBeEmpty();
}
