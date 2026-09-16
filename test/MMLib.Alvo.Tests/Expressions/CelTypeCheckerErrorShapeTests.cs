using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// What a type-checker refusal has to <em>say</em>, as opposed to which expressions it refuses.
/// Every error Alvo emits is read by an agent, so the parts that carry contract are pinned here: the
/// offending operand, the operator, the field, the entity, both operand types, the declared values,
/// and a position that points into the source. The explanatory prose around them deliberately is
/// not — a fact that spells a sentence out breaks on every rewording and proves nothing a caller
/// would notice.
/// </summary>
public class CelTypeCheckerErrorShapeTests
{
    /// <summary>
    /// One row per error site the checker can reach. An empty message — or a fix suggestion that is
    /// present but empty rather than absent — is a defect an agent sees immediately: it is handed a
    /// structured error with nothing in it. Asserted for every site, without asserting any site's
    /// wording.
    /// </summary>
    [Theory]
    [InlineData("@user.id", CelProfile.Computed)]
    [InlineData("!is_public", CelProfile.Mutate)]
    [InlineData("-total", CelProfile.Rule)]
    [InlineData("-status", CelProfile.Computed)]
    [InlineData("'x' in status", CelProfile.Rule)]
    [InlineData("'editor' in @user.roles", CelProfile.Computed)]
    [InlineData("title == 'x'", CelProfile.Mutate)]
    [InlineData("payload == 'x'", CelProfile.Rule)]
    [InlineData("@user.roles == 'editor'", CelProfile.Rule)]
    [InlineData("title > 'a'", CelProfile.Rule)]
    [InlineData("status == 1", CelProfile.Rule)]
    [InlineData("true < false", CelProfile.Rule)]
    [InlineData("status == 'aproved'", CelProfile.Rule)]
    [InlineData("has(title)", CelProfile.Mutate)]
    [InlineData("true ? 1 : 2", CelProfile.Rule)]
    [InlineData("true ? 1 : 'x'", CelProfile.Computed)]
    [InlineData("changed(status)", CelProfile.Rule)]
    [InlineData("!status", CelProfile.Rule)]
    [InlineData("lowerAscii(total)", CelProfile.Mutate)]
    [InlineData("nope == 1", CelProfile.Rule)]
    [InlineData("new.status == 'draft'", CelProfile.Rule)]
    public void Every_refusal_carries_a_message_and_never_an_empty_fix_suggestion(string source, CelProfile profile)
    {
        var result = Compile(source, profile);

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldAllBe(error => !string.IsNullOrWhiteSpace(error.Message));
        result.Errors.ShouldAllBe(error => error.FixSuggestion == null || error.FixSuggestion.Trim().Length > 0);
    }

    /// <summary>
    /// A corrupt schema is an operator's problem, not an author's, so the refusal still carries the
    /// one thing that resolves it.
    /// </summary>
    [Fact]
    public void An_unrecognized_field_type_is_refused_with_a_fix_suggestion()
    {
        var result = CelFixtures.Compiler.Compile("mystery == 1", CelProfile.Rule, CorruptSchema());

        result.Errors[0].FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Deny-by-default all the way down. A profile the allow-list has no row for grants nothing —
    /// not even a literal, the one construct all four declared profiles accept — so a profile added
    /// without wiring compiles in no profile rather than in every one.
    /// </summary>
    [Fact]
    public void A_profile_the_allow_list_does_not_know_grants_nothing_not_even_a_literal()
    {
        var result = Compile("1", (CelProfile)99);

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].Message.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The refusal names the two operators it is about, so an author who wrote one of them is not
    /// left matching a sentence against their source to work out which comparison is meant.
    /// </summary>
    [Fact]
    public void Equality_against_a_null_literal_names_the_operators_it_refuses()
    {
        Compile("owner_id == null", CelProfile.Rule).Errors[0].Message.ShouldContain("'=='");
    }

    /// <summary>
    /// Which operand is wrong, not merely that one is. Two operands of one operator produce two
    /// errors, and an agent that cannot tell them apart has to guess which half to rewrite.
    /// </summary>
    [Theory]
    [InlineData("status && total", CelProfile.Rule, 0, "left operand")]
    [InlineData("status && total", CelProfile.Rule, 1, "right operand")]
    [InlineData("status + owner_id", CelProfile.Computed, 0, "left operand")]
    [InlineData("status + owner_id", CelProfile.Computed, 1, "right operand")]
    [InlineData("!status", CelProfile.Rule, 0, "'!'")]
    [InlineData("-status", CelProfile.Computed, 0, "'-'")]
    [InlineData("total ? 1 : 2", CelProfile.Computed, 0, "ternary")]
    [InlineData("lowerAscii(total)", CelProfile.Mutate, 0, "lowerAscii")]
    public void An_operand_type_error_names_the_operand_it_is_about(
        string source, CelProfile profile, int index, string subject)
    {
        Compile(source, profile).Errors[index].Message.ShouldContain(subject);
    }

    /// <summary>
    /// Arithmetic is refused outside <see cref="CelProfile.Computed"/>, and the refusal names the
    /// operator that was written rather than "arithmetic" in general.
    /// </summary>
    [Theory]
    [InlineData("total + total", "+")]
    [InlineData("total - total", "-")]
    [InlineData("total * total", "*")]
    [InlineData("total / total", "/")]
    public void Arithmetic_refused_outside_computed_names_the_operator(string source, string @operator)
    {
        Compile(source, CelProfile.Rule).Errors[0].Message.ShouldContain($"'{@operator}'");
    }

    /// <summary>
    /// A row-image reference names itself with its qualifier, and names both profiles that accept
    /// one — a hook condition and a before-hook mutate — so the author is told where it does work.
    /// </summary>
    [Theory]
    [InlineData("new.status == 'draft'", "new.status")]
    [InlineData("old.status == 'draft'", "old.status")]
    public void A_row_image_reference_refused_elsewhere_names_itself_and_both_profiles_that_take_it(
        string source, string reference)
    {
        var message = Compile(source, CelProfile.Rule).Errors[0].Message;

        message.ShouldContain(reference);
        message.ShouldContain(nameof(CelProfile.Condition));
        message.ShouldContain(nameof(CelProfile.Mutate));
    }

    [Fact]
    public void A_comparison_type_error_names_both_operand_types()
    {
        var message = Compile("status == 1", CelProfile.Rule).Errors[0].Message;

        message.ShouldContain(nameof(CelValueType.String));
        message.ShouldContain(nameof(CelValueType.Int));
    }

    [Fact]
    public void A_ternary_branch_mismatch_names_both_branch_types()
    {
        var message = Compile("true ? 1 : 'x'", CelProfile.Computed).Errors[0].Message;

        message.ShouldContain(nameof(CelValueType.Int));
        message.ShouldContain(nameof(CelValueType.String));
    }

    [Fact]
    public void Role_membership_refused_in_computed_names_the_operator_and_offers_a_fix()
    {
        var result = Compile("'editor' in @user.roles", CelProfile.Computed);

        result.Errors[1].Message.ShouldContain("'in'");
        result.Errors[1].FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unknown_changed_field_names_the_field_and_the_entity()
    {
        var message = Compile("changed(nope)", CelProfile.Condition).Errors[0].Message;

        message.ShouldContain("nope");
        message.ShouldContain(CelFixtures.Orders.Name);
    }

    [Fact]
    public void An_undeclared_enum_value_names_the_value_and_suggests_the_closest_declared_one()
    {
        var result = Compile("status == 'aproved'", CelProfile.Rule);

        result.Errors[0].Message.ShouldContain("'aproved'");
        result.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("'approved'");
    }

    /// <summary>
    /// With nothing close enough to propose, the suggestion lists the declared values in one
    /// ordinal order — stable across runs — and never proposes an empty candidate.
    /// </summary>
    [Fact]
    public void An_enum_value_with_no_close_match_lists_the_declared_values_in_order()
    {
        var suggestion = Compile("status == 'zzzzzzzz'", CelProfile.Rule).Errors[0].FixSuggestion;

        suggestion.ShouldNotBeNull().ShouldContain("approved, draft");
        suggestion.ShouldNotContain("''");
    }

    /// <summary>
    /// The same rule for an unknown field: no close match means the known fields, listed in one
    /// ordinal order rather than the schema's declaration order.
    /// </summary>
    [Fact]
    public void An_unknown_field_with_no_close_match_lists_the_known_fields_in_order()
    {
        var suggestion = Compile("zzzzzzzzzz == 1", CelProfile.Rule).Errors[0].FixSuggestion;

        suggestion.ShouldNotBeNull().ShouldContain("approved_at, created_at");
    }

    /// <summary>
    /// A position points at the identifier itself, never at a spelling of it embedded in a longer
    /// word or inside a string literal — which is the whole reason the search is boundary-matched
    /// rather than a plain <see cref="string.IndexOf(string, StringComparison)"/>.
    /// </summary>
    [Theory]
    [InlineData("title == 'x_id' && new.id == 1")]
    [InlineData("title == 'xid' && new.id == 1")]
    [InlineData("title == 'idx' && new.id == 1")]
    public void An_error_position_skips_a_spelling_embedded_in_a_longer_word(string source)
    {
        var result = Compile(source, CelProfile.Rule);

        result.Errors[0].Position.ShouldBe(source.IndexOf("new.id", StringComparison.Ordinal) + "new.".Length);
    }

    [Theory]
    [InlineData("title == 'x' && @user.id == owner_id", "@user.id")]
    [InlineData("title == 'x' && 'editor' in @user.roles", "@user.roles")]
    [InlineData("title == 'x' && tenant_id == @tenant.id", "@tenant.id")]
    public void A_context_reference_error_points_at_the_reference(string source, string reference)
    {
        var result = Compile(source, CelProfile.Computed);

        result.Errors[0].Position.ShouldBe(source.IndexOf(reference, StringComparison.Ordinal));
    }

    /// <summary>
    /// The grammar admits whitespace around the dot, so the text the checker looks for is not
    /// always in the source verbatim. A position is an offset an agent indexes the source with, so
    /// "not found" has to fall back inside the string rather than hand out a negative offset.
    /// </summary>
    [Fact]
    public void A_position_stays_inside_the_source_even_when_the_reference_is_written_with_spaces()
    {
        const string source = "@user . id";

        var result = Compile(source, CelProfile.Computed);

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldAllBe(error => error.Position >= 0 && error.Position <= source.Length);
    }

    /// <summary>
    /// The checker's deny-by-default arm for a node kind nothing taught it: refused, and named, so a
    /// future construct added without a case here cannot walk through untyped. It is unreachable
    /// through <see cref="ICelCompiler.Compile"/> — the parser builds only known kinds — so the
    /// checker is exercised directly, which is the only way to prove the arm is there at all.
    /// </summary>
    [Fact]
    public void An_unrecognized_node_kind_is_refused_and_named()
    {
        var (_, _, _, errors) = CelTypeChecker.Check(
            new UnknownNode(), "whatever", CelFixtures.Orders, CelProfile.Rule);

        errors.ShouldHaveSingleItem().Message.ShouldContain(nameof(UnknownNode));
    }

    private static EntitySchema CorruptSchema() => new()
    {
        Name = "weird",
        Fields = [new FieldSchema { Name = "mystery", Type = (FieldType)999 }],
    };

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, CelFixtures.Orders);

    private sealed record UnknownNode : CelNode;
}
