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

    /// <summary>
    /// The maintainer's own request, and the shapes around it that the generated column carries: text joined from
    /// required fields and constants, into a <c>string</c> or <c>text</c> field whose declared length holds it.
    /// </summary>
    /// <param name="fullName">The declaration of <c>customers.full_name</c>.</param>
    [Theory]
    [InlineData("""{ "type": "string", "computed": "first_name + ' ' + last_name" }""")]
    [InlineData("""{ "type": "text", "computed": "last_name + ', ' + first_name" }""")]
    [InlineData("""{ "type": "string", "maxLength": 121, "computed": "first_name + ' ' + last_name" }""")]
    [InlineData("""{ "type": "string", "maxLength": 100, "computed": "(has(middle_name) ? middle_name : '') + last_name" }""")]
    [InlineData("""{ "type": "string", "maxLength": 66, "computed": "tier + ': ' + last_name" }""")]
    [InlineData("""{ "type": "string", "computed": "'Mr. ' + last_name" }""")]
    public void Text_joined_from_required_fields_is_accepted(string fullName)
    {
        _validator.Validate(CustomerDescriptor(fullName)).Errors.ShouldBeEmpty();
    }

    /// <summary>
    /// Each shape the join cannot honestly become a column for is refused at the field, with a fix: a result the
    /// declared type does not hold (either way round), a constant with no field in it, a declared length the join can
    /// exceed — PostgreSQL's <c>varchar(n)</c> would refuse the write where SQLite stores it — and an operand that can
    /// be null.
    /// </summary>
    /// <param name="fullName">The declaration of <c>customers.full_name</c>.</param>
    /// <param name="named">What the refusal must name.</param>
    [Theory]
    [InlineData("""{ "type": "integer", "computed": "first_name + ' ' + last_name" }""", "string")]
    [InlineData("""{ "type": "enum", "values": ["a"], "computed": "first_name + last_name" }""", "string")]
    [InlineData("""{ "type": "string", "computed": "visits + visits" }""", "Int")]
    [InlineData("""{ "type": "string", "computed": "'always the same'" }""", "no field")]
    [InlineData("""{ "type": "string", "computed": "'a' + 'b'" }""", "no field")]
    [InlineData("""{ "type": "string", "maxLength": 100, "computed": "first_name + ' ' + last_name" }""", "121")]
    [InlineData("""{ "type": "string", "maxLength": 100, "computed": "first_name + bio" }""", "bio")]
    [InlineData("""{ "type": "string", "maxLength": 100, "computed": "first_name + nickname" }""", "nickname")]
    [InlineData("""{ "type": "string", "computed": "first_name + middle_name" }""", "middle_name")]
    public void Text_the_column_cannot_honestly_hold_is_refused_at_the_field(string fullName, string named)
    {
        var error = _validator.Validate(CustomerDescriptor(fullName)).Errors.ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/customers/fields/full_name/computed");
        error.Message.ShouldContain(named);
        error.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A text constant compared inside a condition is still a bound value — and the refusal says that is the cause,
    /// rather than claiming text constants are not carried at all.
    /// </summary>
    [Fact]
    public void A_compared_text_constant_is_refused_naming_the_comparison()
    {
        var error = _validator.Validate(CustomerDescriptor(
                """{ "type": "string", "computed": "first_name == 'Jana' ? 'J' : last_name" }"""))
            .Errors.ShouldHaveSingleItem();

        error.Message.ShouldContain("a text constant can be joined, not compared, in a computed field");
        error.Message.ShouldContain("'Jana'");
    }

    /// <summary>
    /// A computed field that reads another computed field is refused at validation, in engine-neutral words:
    /// PostgreSQL refuses such a generation expression at apply while SQLite accepts it. The fix is the other field's
    /// own expression, written in its place.
    /// </summary>
    /// <param name="computed">The declaration of <c>greeting</c>, over the computed <c>full_name</c>.</param>
    [Theory]
    [InlineData("'Dear ' + full_name")]
    [InlineData("has(full_name) ? full_name : last_name")]
    public void A_computed_field_reading_another_computed_field_is_refused(string computed)
    {
        var json = $$"""
            { "apiVersion": "alvo.dev/v1", "name": "demo",
              "entities": { "customers": { "fields": {
                "first_name": { "type": "string", "required": true },
                "last_name": { "type": "string", "required": true },
                "full_name": { "type": "string", "required": true, "computed": "first_name + ' ' + last_name" },
                "greeting": { "type": "string", "computed": "{{computed}}" } } } } }
            """;

        var error = _validator.Validate(json).Errors.ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/customers/fields/greeting/computed");
        error.Message.ShouldContain("'full_name'");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("first_name + ' ' + last_name");
    }

    /// <summary>
    /// A computed field that reads itself gets its own message rather than the reads-another-computed-field one: no
    /// engine accepts a generated column that is its own input, so there is no "the other engine accepts it" to name,
    /// and the fix cannot be "write its own expression in its place" (that expression is itself).
    /// </summary>
    [Fact]
    public void A_computed_field_reading_itself_is_refused_naming_the_self_reference()
    {
        var json = """
            { "apiVersion": "alvo.dev/v1", "name": "demo",
              "entities": { "customers": { "fields": {
                "greeting": { "type": "string", "required": true, "computed": "greeting + 'x'" } } } } }
            """;

        var error = _validator.Validate(json).Errors.ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/customers/fields/greeting/computed");
        error.Message.ShouldContain("cannot read itself");
        error.Message.ShouldNotContain("SQLite accepts it", Case.Sensitive, "SQLite refuses a generated-column loop too");
        error.FixSuggestion.ShouldNotBeNull()
            .ShouldNotContain("greeting + 'x'", Case.Sensitive, "the fix must not be circular");
    }

    /// <summary>A rollup is a stored column the framework maintains, so a computed field may read it (the ladder).</summary>
    [Fact]
    public void A_computed_field_may_still_read_a_rollup()
    {
        var json = """
            { "apiVersion": "alvo.dev/v1", "name": "demo",
              "entities": {
                "invoices": { "fields": {
                  "vat": { "type": "decimal", "precision": 18, "scale": 2 },
                  "net": { "type": "decimal", "precision": 18, "scale": 2, "rollup": { "from": "lines", "op": "sum", "field": "amount" } },
                  "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "net + vat" } } },
                "lines": { "fields": {
                  "invoice_id": { "type": "ref", "entity": "invoices", "required": true },
                  "amount": { "type": "decimal", "precision": 18, "scale": 2 } } } } }
            """;

        _validator.Validate(json).Errors.ShouldBeEmpty();
    }

    private static string CustomerDescriptor(string fullName) => $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "customers": { "fields": {
            "first_name": { "type": "string", "required": true, "maxLength": 60 },
            "last_name": { "type": "string", "required": true, "maxLength": 60 },
            "middle_name": { "type": "string", "maxLength": 40 },
            "nickname": { "type": "string", "required": true },
            "bio": { "type": "text", "required": true },
            "tier": { "type": "enum", "values": ["none", "club"], "required": true },
            "visits": { "type": "integer", "required": true },
            "full_name": {{fullName}} } } } }
        """;

    private static string Descriptor(string computed) => $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "net": { "type": "decimal", "precision": 18, "scale": 2 },
            "vat": { "type": "decimal", "precision": 18, "scale": 2 },
            "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "{{computed}}" } } } } }
        """;
}
