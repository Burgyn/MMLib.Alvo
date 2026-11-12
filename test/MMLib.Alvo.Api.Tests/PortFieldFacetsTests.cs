using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Schema;

using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The data port's facet measure (issue 481): the payload validator's own checks, over the values a host write supplies —
/// so a refusal rendered over HTTP is the violation the body validator would have produced for the same value.
/// </summary>
public sealed class PortFieldFacetsTests
{
    private static readonly EntitySchema _tickets = new()
    {
        Name = "tickets",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Required = true, MaxLength = 10 },
            new FieldSchema { Name = "priority", Type = FieldType.Enum, EnumValues = ["low", "high"] },
            new FieldSchema { Name = "secret", Type = FieldType.String, MaxLength = 5 },
        ],
    };

    private static readonly PortFieldFacets _facets = new();

    private static readonly IReadOnlySet<string> _nothingHidden = FrozenSet<string>.Empty;

    [Theory]
    [InlineData("title", "eleven char")]
    [InlineData("priority", "urgent")]
    public async Task A_refusal_over_http_is_the_violation_the_body_validator_reports_for_the_same_value(string field, string value)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = value };

        var refused = _facets.Refusal(_tickets, values, _nothingHidden).ShouldNotBeNull();
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(refused).ShouldNotBeNull());

        var expected = (await RecordValidator.ValidateAsync(Request(values), TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        status.ShouldBe(422);
        var written = document["violations"]!.AsArray().ShouldHaveSingleItem()!;
        (written["pointer"]!.GetValue<string>(), written["code"]!.GetValue<string>(), written["message"]!.GetValue<string>(), written["fixSuggestion"]!.GetValue<string>())
            .ShouldBe((expected.Pointer, expected.Code, expected.Message, expected.FixSuggestion!));
        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, field));
    }

    [Fact]
    public void A_refusal_for_a_hidden_field_names_no_field_and_carries_the_fixed_wording()
    {
        var refused = _facets.Refusal(_tickets, Values(("secret", "too long")), new HashSet<string>(StringComparer.Ordinal) { "secret" })
            .ShouldNotBeNull();

        (refused.Kind, refused.Fields.Count, refused.FacetCode).ShouldBe((AlvoConstraintKind.Check, 0, (string?)null));
        refused.Message.ShouldBe(new AlvoConstraintViolationException(AlvoConstraintKind.Check, []).Message);
    }

    [Fact]
    public void Only_the_supplied_non_null_values_are_measured()
    {
        _facets.Refusal(_tickets, Values(("priority", "low")), _nothingHidden).ShouldBeNull("an omitted required field keeps its stored value");
        _facets.Refusal(_tickets, Values(("priority", null)), _nothingHidden).ShouldBeNull("a null is the required rule's, not a facet's");
        _facets.Refusal(_tickets, Values(("undeclared", "x")), _nothingHidden).ShouldBeNull("an undeclared key is the payload guard's");
    }

    [Fact]
    public void The_first_field_in_declaration_order_is_the_one_refused()
    {
        var refused = _facets.Refusal(_tickets, Values(("priority", "urgent"), ("title", "eleven char")), _nothingHidden).ShouldNotBeNull();

        refused.Fields.ShouldHaveSingleItem().ShouldBe("title");
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_left_to_the_driver_s_type_check()
    {
        _facets.Refusal(_tickets, Values(("title", 12345678901L)), _nothingHidden).ShouldBeNull();
    }

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    private static RecordValidationRequest Request(IReadOnlyDictionary<string, object?> values) => new(
        _tickets,
        values,
        IsCreate: false,
        ReadOnlyFields: new HashSet<string>(StringComparer.Ordinal),
        AlreadyReported: new HashSet<string>(StringComparer.Ordinal),
        FormatCatalog.Build([_tickets]),
        Data: null!,
        AlvoContext.Anonymous);
}
