using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A <c>computed</c> expression is compiled — and rendered — when the descriptor is validated, so an expression the
/// generated column cannot carry is a structured refusal at save time rather than an exception at the first plan.
/// </summary>
/// <remarks>
/// The security core's fail-fast-compile rule, applied to the one CEL slot the validator used to skip: before this,
/// <c>"computed": "now()"</c> validated clean and then threw an undocumented <see cref="InvalidOperationException"/>
/// out of the EF driver's model builder on Preview.
/// </remarks>
public class ComputedFieldValidationTests
{
    private const string Pointer = "/entities/invoices/fields/gross/computed";

    private static readonly DescriptorValidator _validator = new();

    /// <summary>Every kind of expression the generated column cannot carry is refused at the field's own pointer.</summary>
    /// <param name="computed">The declared <c>computed</c> expression.</param>
    /// <param name="because">What makes it one the column cannot carry.</param>
    [Theory]
    [InlineData("now()", "a function call is legal only in a before-hook's mutate")]
    [InlineData("@user.id", "a generated column has no caller")]
    [InlineData("old.net", "a generated column has no before-image")]
    [InlineData("changed(net)", "a generated column has no before-image")]
    [InlineData("netto + vat", "the field does not exist")]
    [InlineData("net * 1.2", "a constant is a bind parameter, and DDL has none")]
    [InlineData("net + vat > net ? net : vat", "the renderer cannot compare an arithmetic operand")]
    public void An_expression_the_generated_column_cannot_carry_is_refused_at_the_field(string computed, string because)
    {
        var errors = _validator.Validate(Descriptor(computed)).Errors;

        errors.ShouldNotBeEmpty(because);
        errors.ShouldAllBe(error => error.Path == Pointer, "the pointer names the computed key, not the whole document");
        errors.ShouldAllBe(error => error.Severity == DescriptorValidationSeverity.Error);
        errors.ShouldAllBe(error => error.Message.Contains("invoices.gross"), "the message names the field");
        errors.ShouldAllBe(error => !string.IsNullOrWhiteSpace(error.FixSuggestion), "every refusal says what to do");
    }

    /// <summary>The shapes the sources give, and the column carries, still validate clean.</summary>
    /// <param name="computed">The declared <c>computed</c> expression.</param>
    [Theory]
    [InlineData("net + vat")]
    [InlineData("net * vat - net")]
    [InlineData("net > vat ? net : vat")]
    [InlineData("has(vat) ? net + vat : net")]
    public void An_expression_the_generated_column_carries_is_accepted(string computed)
    {
        _validator.Validate(Descriptor(computed)).Errors.ShouldBeEmpty();
    }

    /// <summary>Two computed fields that each fail are both reported: one round trip, every finding.</summary>
    [Fact]
    public void Every_refused_computed_field_is_reported_rather_than_the_first()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "net": { "type": "decimal", "precision": 18, "scale": 2 },
            "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "now()" },
            "stamp": { "type": "datetime", "computed": "now()" } } } } }
        """;

        _validator.Validate(json).Errors.Select(error => error.Path).Distinct().ShouldBe(
            [Pointer, "/entities/invoices/fields/stamp/computed"], ignoreOrder: true);
    }

    private static string Descriptor(string computed) => $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "net": { "type": "decimal", "precision": 18, "scale": 2 },
            "vat": { "type": "decimal", "precision": 18, "scale": 2 },
            "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "{{computed}}" } } } } }
        """;
}
