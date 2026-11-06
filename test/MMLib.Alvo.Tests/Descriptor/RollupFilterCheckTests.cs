using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A <c>rollup.where</c> is a Rule-profile predicate over the child, under the stored-predicate surface (HX-11 Ruling 31):
/// a filter the recompute cannot carry is a structured refusal at its own pointer when the descriptor is saved.
/// </summary>
public sealed class RollupFilterCheckTests
{
    [Fact]
    public void A_filter_over_the_child_s_fields_is_valid() =>
        RollupDescriptors.Errors(where: "status == 'held'").ShouldBeEmpty();

    [Theory]
    [InlineData("product_id == @user.id", "@user")]
    [InlineData("created_at > now() - duration('24h')", "now()")]
    [InlineData("missing == 'x'", "missing")]
    [InlineData("quantity", "boolean")]
    public void A_filter_with_a_caller_a_clock_or_an_unknown_field_is_refused_at_its_pointer(string where, string named)
    {
        var error = RollupDescriptors.Errors(where).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/held_total/rollup/where");
        error.Message.ShouldContain(named);
    }

    /// <summary>
    /// Ruling 69, pinned: a list literal never reaches the stored-predicate surface — the parser refuses it, and the
    /// refusal names the equality-chain rewrite rather than the caller's role.
    /// </summary>
    [Fact]
    public void A_list_literal_in_a_filter_is_refused_at_its_pointer_with_the_parser_s_text()
    {
        var error = RollupDescriptors.Errors("status in ['held', 'released']").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/held_total/rollup/where");
        error.Message.ShouldContain("no list literals");
    }

    /// <summary>
    /// Ruling 71: a filtered aggregate is a predicate oracle over every child field its filter reads, so a filter over a
    /// hidden child field — static or role-dependent, the notion a validation's read set uses — is refused at its pointer.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("\"!('admin' in @user.roles)\"")]
    public void A_filter_over_a_hidden_child_field_is_refused_at_its_pointer(string hidden)
    {
        var error = RollupDescriptors.Errors("label == 'x'", labelHidden: hidden).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/held_total/rollup/where");
        error.Message.ShouldContain("hidden");
        error.Message.ShouldContain("'reservations.label'", Case.Sensitive, "named as '<entity>.<field>', so a read reached through a grandchild is findable (Task 11 review m1)");
    }

    [Fact]
    public void A_filter_beside_a_hidden_child_field_it_does_not_read_is_valid() =>
        RollupDescriptors.Errors("status == 'held'", labelHidden: "true").ShouldBeEmpty();

    [Fact]
    public void A_filter_is_no_longer_an_unhonoured_slot() =>
        UnhonouredFeatures.EveryRefusal.ShouldNotContain(refusal => refusal.Slot == "rollup.where");

    /// <summary>A text constant no SQL literal can carry is refused by the renderer, past the compiler.</summary>
    [Fact]
    public void A_text_constant_a_sql_literal_cannot_carry_is_refused_at_its_pointer() =>
        RollupDescriptors.Errors("label == 'a\u0001b'").ShouldHaveSingleItem().Path
            .ShouldBe("/entities/products/fields/held_total/rollup/where");

    /// <summary>
    /// <c>products.held_total = sum(reservations.quantity) where &lt;where&gt;</c>, validated through
    /// <see cref="DescriptorValidator"/>.
    /// </summary>
    private static class RollupDescriptors
    {
        private const string Rules = """{"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}""";

        internal static IReadOnlyList<DescriptorValidationError> Errors(string where, string? labelHidden = null)
        {
            var label = labelHidden is null ? "{\"type\": \"string\"}" : $"{{\"type\": \"string\", \"hidden\": {labelHidden}}}";
            var rollup = $$$"""{"type": "decimal", "precision": 18, "scale": 2, "rollup": {"from": "reservations", "op": "sum", "field": "quantity", "where": {{{Quoted(where)}}}}}""";
            var json = "{\"$schema\": \"https://alvo.dev/schema/v1/project.json\", \"apiVersion\": \"alvo.dev/v1\", \"name\": \"filters\", \"entities\": {"
                + $"\"products\": {{\"fields\": {{\"held_total\": {rollup}}}, \"rules\": {Rules}}}, "
                + "\"reservations\": {\"audit\": true, \"fields\": {\"product_id\": {\"type\": \"ref\", \"entity\": \"products\", \"required\": true}, "
                + "\"quantity\": {\"type\": \"decimal\", \"precision\": 18, \"scale\": 2, \"required\": true}, "
                + "\"status\": {\"type\": \"enum\", \"values\": [\"held\", \"released\"]}, "
                + $"\"label\": {label}}}, \"rules\": {Rules}}}}}}}";
            return [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
        }

        private static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text);
    }
}
