using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The invariant <see cref="BoundValue.FromPolicyPredicate"/> rests on: a rendered policy predicate's values
/// are bound by their own CLR type, and that is safe only because the CEL grammar cannot express a value
/// whose type disagrees with its column in a way the dialect's operand repair does not already cover.
/// </summary>
/// <remarks>
/// The dangerous case is temporal: <see cref="CelFieldType"/> collapses <c>date</c> and <c>timestamp</c> into
/// one <see cref="CelValueType.Timestamp"/>, so a type checker that admitted a temporal literal would let a
/// <c>date</c> column be compared against a <see cref="DateTimeOffset"/> — which on SQLite matches nothing and
/// raises nothing, fail-closed under a positive comparison and fail-open under a negated one. It is
/// unreachable because <b>the grammar has no temporal literal at all</b>. If one is ever added these facts
/// fail, and the argument has to be replaced by carrying the compared field through <c>SqlPredicate</c>.
/// </remarks>
public class CelRuleBindingTests
{
    /// <summary>
    /// Every literal form the grammar admits, against both temporal field types. All must be refused at
    /// <em>compile</em> time, which is where Alvo requires a bad rule to fail.
    /// </summary>
    [Theory]
    [InlineData("due_on == '2026-01-02'")]
    [InlineData("due_on != '2026-01-02'")]
    [InlineData("due_on > '2026-01-02'")]
    [InlineData("due_on == 1")]
    [InlineData("due_on >= 1")]
    [InlineData("due_on == 1.5")]
    [InlineData("due_on == true")]
    [InlineData("created_at == '1970-01-02T00:00:00'")]
    [InlineData("created_at >= '1970-01-02T00:00:00'")]
    [InlineData("created_at > 0")]
    [InlineData("created_at == 1.5")]
    [InlineData("created_at == true")]
    public void No_rule_can_compare_a_temporal_field_against_a_literal(string rule)
        => Compile(rule).IsSuccess.ShouldBeFalse();

    [Theory]
    [InlineData("due_on == @user.id")]
    [InlineData("created_at == @tenant.id")]
    public void No_rule_can_compare_a_temporal_field_against_a_context_value(string rule)
        => Compile(rule).IsSuccess.ShouldBeFalse();

    /// <summary>
    /// The two shapes a temporal field <em>can</em> legally take in a rule bind no value at all, so neither
    /// reaches the binder: presence is an <c>IS NOT NULL</c> test and a field-to-field comparison has two
    /// columns and no parameter.
    /// </summary>
    [Theory]
    [InlineData("has(due_on)")]
    [InlineData("!has(created_at)")]
    [InlineData("due_on == created_at")]
    public void A_legal_temporal_rule_binds_no_value(string rule)
        => Render(rule).Parameters.ShouldBeEmpty();

    /// <summary>
    /// The positive control, so the facts above cannot pass because nothing compiles at all: a rule the
    /// grammar does admit binds its value, and binds it as the CLR type the column holds.
    /// </summary>
    [Fact]
    public void A_rule_over_a_uuid_field_binds_a_guid()
        => Render("owner_id == @user.id").Parameters.Values.ShouldAllBe(value => value is Guid);

    [Fact]
    public void A_rule_over_a_string_field_binds_a_string()
        => Render("status == 'open'").Parameters.Values.ShouldAllBe(value => value is string);

    /// <summary>
    /// The one reachable numeric mismatch, and the reason it is harmless: an integer literal against a
    /// decimal column promotes to a <see cref="CelValueType.Decimal"/> comparison, whose operands the
    /// dialect repairs on both sides.
    /// </summary>
    [Fact]
    public void An_integer_literal_against_a_decimal_column_is_repaired_rather_than_compared_raw()
    {
        var rendered = Render("price > 100", new TypeMarkingFieldSqlRenderer());

        rendered.Sql.ShouldContain("CMP<Decimal>");
    }

    private static SqlPredicate Render(string rule, IFieldSqlRenderer? fields = null)
    {
        using var services = Services();
        var compiled = Compile(rule, services);
        compiled.IsSuccess.ShouldBeTrue();

        return services.GetRequiredService<IPredicateRenderer>().Render(
            compiled.Expression!,
            AlvoDataFixtures.Caller,
            fields ?? new TestFieldSqlRenderer(),
            PolicyParameterPrefix.Using);
    }

    private static CelCompilationResult Compile(string rule)
    {
        using var services = Services();
        return Compile(rule, services);
    }

    private static CelCompilationResult Compile(string rule, IServiceProvider services) =>
        services.GetRequiredService<ICelCompiler>().Compile(rule, CelProfile.Rule, AlvoDataFixtures.Vehicle);

    private static ServiceProvider Services() => new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
}
