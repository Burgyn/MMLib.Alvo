using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// An index's <c>exclude</c> is a half-open range of two required temporal fields of one type, compared per equal
/// <c>fields</c> — so a range the engine cannot keep is a structured refusal at
/// <c>/entities/&lt;e&gt;/indexes/&lt;i&gt;/exclude[/from|/to]</c> when the descriptor is saved (D1, D17, D18).
/// </summary>
public sealed class ExclusionIndexCheckTests
{
    private const string Pointer = "/entities/bookings/indexes/0/exclude";

    [Fact]
    public void A_valid_exclusion_has_no_findings()
    {
        Validate(Exclusion()).ShouldBeEmpty();
        Validate(Exclusion(where: "status != 'cancelled'")).ShouldBeEmpty();
        Validate(Exclusion(from: "day_from", to: "day_to")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("missing", "/from", "does not declare")]
    [InlineData("note_at", "/from", "is not required")]
    [InlineData("status", "/from", "is not a date or a datetime")]
    public void A_range_field_that_is_missing_optional_or_not_temporal_is_refused(string from, string at, string reason)
    {
        var error = Check(Exclusion(from: from)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + at);
        error.Message.ShouldContain(reason);
    }

    [Fact]
    public void Range_fields_of_different_types_are_refused()
    {
        var error = Check(Exclusion(from: "day_from", to: "ends_at")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/to");
        error.Message.ShouldBe(
            "An index of 'bookings' excludes overlapping ranges from 'day_from' (date) to 'ends_at' (datetime) — both ends must be of one type.");
    }

    [Fact]
    public void A_range_field_listed_in_fields_is_refused()
    {
        var error = Check(Exclusion(fields: "\"room\", \"starts_at\"")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/from");
        error.Message.ShouldContain("is also one of the index's 'fields'");
    }

    [Fact]
    public void An_equality_field_of_type_json_is_refused()
    {
        var error = Check(Exclusion(fields: "\"room\", \"extra\"")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldContain("'extra'");
        error.Message.ShouldContain("json");
    }

    [Fact]
    public void An_exclusion_marked_unique_is_refused()
    {
        var error = Check(Exclusion(unique: true)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.FixSuggestion.ShouldBe("Remove 'unique', or declare the unique index separately.");
    }

    [Theory]
    [InlineData("computed_at")]
    [InlineData("rolled_at")]
    [InlineData("numbered_at")]
    public void A_computed_rollup_or_sequence_range_field_is_refused(string to)
    {
        var errors = Check(Exclusion(to: to));
        errors.ShouldContain(error => error.Path == Pointer + "/to" && error.Message.Contains("maintained by the framework", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hidden_range_field_is_refused()
    {
        var error = Check(Exclusion(to: "secret_end")).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/to");
        error.Message.ShouldBe(
            "An index of 'bookings' excludes overlapping ranges ending at 'secret_end', which is hidden: a refused write would "
            + "disclose where a range its readers cannot see lies.");
        Check(Exclusion(fields: "\"room\", \"secret_room\"")).ShouldBeEmpty("a hidden equality field is the oracle a unique index already is (D18)");
    }

    [Fact]
    public void An_exclusion_on_a_dynamic_entity_is_refused_naming_41()
    {
        var error = Validate(Exclusion(), storage: "dynamic").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe(
            "An index of 'bookings' declares 'exclude', which the database keeps as an exclusion constraint, and 'bookings' "
            + "declares 'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.");
        error.FixSuggestion.ShouldBe("Remove 'exclude', or make 'bookings' a physical entity.");
    }

    [Fact]
    public void The_where_beside_an_exclude_is_checked_as_an_index_filter() =>
        Validate(Exclusion(where: "missing == 'x'")).ShouldHaveSingleItem().Path.ShouldBe("/entities/bookings/indexes/0/where");

    /// <summary>
    /// D4: the mapper folds the range's <c>to &gt;= from</c> into the end field's validation — ANDed with one it declares —
    /// so an inverted range is the end field's CHECK, and maps the exclusion onto the index.
    /// </summary>
    [Fact]
    public void An_exclusion_maps_onto_its_index_and_orders_its_range_with_the_end_field_s_validation()
    {
        var json = Json(Exclusion(where: "status != 'cancelled'"), "physical", string.Empty)
            .Replace("\"ends_at\": {\"type\": \"datetime\", \"required\": true}", "\"ends_at\": {\"type\": \"datetime\", \"required\": true, \"validation\": \"value != starts_at\"}", StringComparison.Ordinal);

        var bookings = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(json)).Entities.ShouldHaveSingleItem();

        bookings.Fields.Single(field => field.Name == "ends_at").CheckExpression.ShouldBe("(ends_at != starts_at) && ends_at >= starts_at");
        var index = bookings.Indexes.ShouldHaveSingleItem();
        (index.Exclude, index.Where).ShouldBe((new MMLib.Alvo.Schema.ExclusionSchema("starts_at", "ends_at"), "status != 'cancelled'"));
    }

    private static string Exclusion(
        string fields = "\"room\"", string from = "starts_at", string to = "ends_at", bool unique = false, string? where = null) =>
        $$"""{"fields": [{{fields}}], "exclude": {"from": "{{from}}", "to": "{{to}}"}{{(unique ? ", \"unique\": true" : "")}}{{(where is null ? "" : $", \"where\": {JsonSerializer.Serialize(where)}")}}}""";

    private static IReadOnlyList<DescriptorValidationError> Check(string index) =>
        ExclusionIndexCheck.Check(AlvoDescriptor.Parse(Json(index, "physical", Maintained)));

    private static IReadOnlyList<DescriptorValidationError> Validate(string index, string storage = "physical") =>
        [.. new DescriptorValidator().Validate(Json(index, storage, string.Empty)).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private const string Maintained =
        """, "computed_at": {"type": "datetime", "computed": "starts_at"},"""
        + """ "rolled_at": {"type": "datetime", "required": true, "rollup": {"from": "bookings", "op": "max", "field": "starts_at", "via": "room"}},"""
        + """ "numbered_at": {"type": "integer", "sequence": {}}""";

    private static string Json(string index, string storage, string maintained)
    {
        const string Fields =
            """{"room": {"type": "string", "required": true}, "status": {"type": "string"}, "extra": {"type": "json"},"""
            + """ "starts_at": {"type": "datetime", "required": true}, "ends_at": {"type": "datetime", "required": true},"""
            + """ "day_from": {"type": "date", "required": true}, "day_to": {"type": "date", "required": true},"""
            + """ "note_at": {"type": "datetime"},"""
            + """ "secret_end": {"type": "datetime", "required": true, "hidden": true}, "secret_room": {"type": "string", "required": true, "hidden": true}""";
        return $$"""{"$schema": "https://alvo.dev/schema/v1/project.json", "apiVersion": "alvo.dev/v1", "name": "exclusions", "entities": {"bookings": {"storage": "{{storage}}", "fields": {{Fields}}{{maintained}}},"""
            + $$""" "indexes": [{{index}}],"""
            + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""";
    }
}
