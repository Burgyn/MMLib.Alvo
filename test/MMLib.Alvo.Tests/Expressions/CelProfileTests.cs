using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Asserts the profile allow-list table: which constructs each of the three profiles accepts, and
/// the corrected rule for Rule/Condition/Computed's required result type (a binding decision on
/// top of the original brief — see the deviations below).
/// </summary>
/// <remarks>
/// The brief's own profile table forbade comparisons in <see cref="CelProfile.Computed"/> while
/// allowing the ternary, whose condition must itself be a comparison — a contradiction. The
/// corrected rule this test asserts: Computed allows comparisons, <c>&amp;&amp;</c>/<c>||</c>/<c>!</c>,
/// arithmetic, <c>has</c>, and the ternary, but never a context reference or <c>old</c>/<c>new</c>/
/// <c>changed</c>; and Computed's whole expression must be a non-boolean <em>scalar</em> — not
/// <c>Bool</c>, not <c>Json</c>, not <c>Null</c>, not a role list — not merely "not forbidden of
/// the wrong node kind", so a bare comparison is rejected there for its result type, not for using
/// a banned operator.
/// </remarks>
public class CelProfileTests
{
    [Fact]
    public void Arithmetic_is_computed_only()
    {
        CelFixtures.Compiler.Compile("total + total", CelProfile.Rule, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();

        CelFixtures.CompileComputed("total + total").ResultType.ShouldBe(CelValueType.Decimal);
    }

    [Fact]
    public void Context_references_are_unavailable_in_computed()
    {
        var result = CelFixtures.Compiler.Compile("@user.id", CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].Message.ShouldContain("A computed column is evaluated by the database with no caller context");
    }

    [Fact]
    public void Changed_is_condition_only()
    {
        CelFixtures.Compiler.Compile("changed(status)", CelProfile.Rule, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();

        CelFixtures.CompileCondition("changed(status)").ResultType.ShouldBe(CelValueType.Bool);
    }

    [Fact]
    public void New_and_old_field_qualifiers_are_condition_only()
    {
        CelFixtures.Compiler.Compile("new.status == 'draft'", CelProfile.Rule, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();

        CelFixtures.CompileCondition("new.status == 'draft'").ResultType.ShouldBe(CelValueType.Bool);
    }

    /// <summary>
    /// Every profile there is, enumerated from the enum rather than listed — so a profile added later
    /// cannot escape the claim this fact's name makes.
    /// </summary>
    /// <remarks>
    /// It was three <c>InlineData</c> rows until <see cref="CelProfile.Mutate"/> landed, and a fourth row
    /// is the fix that looks right and is not: the next profile would have been missing again, and the
    /// fact would still have been named "in every profile". A fact whose coverage has to be remembered is
    /// a fact that will eventually be wrong about itself.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryProfile))]
    public void A_comprehension_macro_is_rejected_in_every_profile_toward_a_hook(CelProfile profile)
    {
        var result = CelFixtures.Compiler.Compile("all(f, f > 0)", profile, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("hooks.beforeUpdate");
    }

    public static TheoryData<CelProfile> EveryProfile()
    {
        TheoryData<CelProfile> profiles = [];
        foreach (var profile in Enum.GetValues<CelProfile>())
        {
            profiles.Add(profile);
        }

        return profiles;
    }

    [Fact]
    public void A_bare_comparison_is_rejected_in_computed_for_its_result_type_not_its_operator()
    {
        var result = CelFixtures.Compiler.Compile("status == 'draft'", CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].Message.ShouldContain("non-boolean scalar");
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("null")]
    [InlineData("@user.roles")]
    public void Computed_rejects_every_non_scalar_result_type(string source)
    {
        CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void A_ternary_over_a_comparison_condition_is_the_computed_escape_hatch()
    {
        var expression = CelFixtures.CompileComputed("status == 'draft' ? 1 : 2");

        expression.ResultType.ShouldBe(CelValueType.Int);
    }

    [Fact]
    public void Computed_allows_the_logical_and_comparison_operators_the_original_table_wrongly_banned()
    {
        var expression = CelFixtures.CompileComputed("(status == 'draft' && total > 0) ? 1 : 2");

        expression.ResultType.ShouldBe(CelValueType.Int);
    }

    [Fact]
    public void Role_membership_is_unavailable_in_computed()
    {
        var result = CelFixtures.Compiler.Compile("'editor' in @user.roles", CelProfile.Computed, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Has_is_legal_in_every_profile()
    {
        CelFixtures.CompileRule("has(owner_id)").ResultType.ShouldBe(CelValueType.Bool);
        CelFixtures.CompileCondition("has(owner_id)").ResultType.ShouldBe(CelValueType.Bool);

        CelFixtures.Compiler.Compile("has(owner_id) ? 1 : 2", CelProfile.Computed, CelFixtures.Orders)
            .IsSuccess.ShouldBeTrue();
    }
}
