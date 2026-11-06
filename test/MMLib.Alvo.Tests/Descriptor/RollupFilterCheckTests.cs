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

        internal static IReadOnlyList<DescriptorValidationError> Errors(string where)
        {
            var rollup = $$$"""{"type": "decimal", "precision": 18, "scale": 2, "rollup": {"from": "reservations", "op": "sum", "field": "quantity", "where": {{{Quoted(where)}}}}}""";
            var json = "{\"$schema\": \"https://alvo.dev/schema/v1/project.json\", \"apiVersion\": \"alvo.dev/v1\", \"name\": \"filters\", \"entities\": {"
                + $"\"products\": {{\"fields\": {{\"held_total\": {rollup}}}, \"rules\": {Rules}}}, "
                + "\"reservations\": {\"audit\": true, \"fields\": {\"product_id\": {\"type\": \"ref\", \"entity\": \"products\", \"required\": true}, "
                + "\"quantity\": {\"type\": \"decimal\", \"precision\": 18, \"scale\": 2, \"required\": true}, "
                + "\"status\": {\"type\": \"enum\", \"values\": [\"held\", \"released\"]}, "
                + $"\"label\": {{\"type\": \"string\"}}}}, \"rules\": {Rules}}}}}}}";
            return [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
        }

        private static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text);
    }
}
