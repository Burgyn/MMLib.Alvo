using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A <c>virtual</c> field has no column, so every key that stores, constrains, indexes or references a value is refused
/// beside it at apply, each at the pointer an agent can fix.
/// </summary>
public sealed class VirtualFieldCheckTests
{
    private const string NoColumnTail = "a virtual field has no column, so nothing stores, constrains, indexes or references it.";

    [Theory]
    [InlineData("computed", "\"computed\": \"title + '!'\"")]
    [InlineData("rollup", "\"rollup\": {\"from\": \"comments\", \"op\": \"count\"}")]
    [InlineData("default", "\"default\": \"x\"")]
    [InlineData("required", "\"required\": true")]
    [InlineData("nullable", "\"nullable\": false")]
    [InlineData("unique", "\"unique\": true")]
    [InlineData("index", "\"index\": true")]
    [InlineData("validation", "\"validation\": \"size(value) > 1\"")]
    [InlineData("renamedFrom", "\"renamedFrom\": \"label\"")]
    [InlineData("entity", "\"type\": \"ref\", \"entity\": \"comments\"")]
    [InlineData("sequence", "\"type\": \"integer\", \"sequence\": {}")]
    public void A_virtual_field_combined_with_a_storage_key_is_refused_at_its_virtual_pointer(string key, string json)
    {
        var display = json.Contains("\"type\"", StringComparison.Ordinal)
            ? $"{{\"virtual\": true, {json}}}"
            : $"{{\"type\": \"string\", \"virtual\": true, {json}}}";

        var error = VirtualErrors(Notes(display)).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/notes/fields/display/virtual");
        error.Message.ShouldBe($"Field 'notes.display' is virtual and declares '{key}': {NoColumnTail}");
        error.FixSuggestion.ShouldBe($"Remove '{key}', or drop 'virtual' to store the field.");
    }

    [Fact]
    public void A_virtual_field_in_an_index_is_refused_at_the_index()
    {
        var error = VirtualErrors(Notes(Virtual, indexes: "[{\"fields\": [\"title\", \"display\"]}]")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/notes/indexes/0/fields");
        error.Message.ShouldBe($"Field 'notes.display' is virtual: {NoColumnTail}");
    }

    [Theory]
    [InlineData("from", "{\"from\": \"display\", \"to\": \"title\"}")]
    [InlineData("to", "{\"from\": \"title\", \"to\": \"display\"}")]
    public void A_virtual_field_as_an_exclusion_s_range_end_is_refused_at_that_end(string end, string exclude)
    {
        var error = VirtualErrors(Notes(Virtual, indexes: $"[{{\"fields\": [\"title\"], \"exclude\": {exclude}}}]")).ShouldHaveSingleItem();

        error.Path.ShouldBe($"/entities/notes/indexes/0/exclude/{end}");
    }

    [Fact]
    public void A_rollup_over_a_virtual_child_field_is_refused_at_the_rollup()
    {
        var error = VirtualErrors(Notes(
            "{\"type\": \"string\"}",
            commentScore: "{\"type\": \"integer\", \"virtual\": true}",
            noteRollup: "{\"type\": \"integer\", \"rollup\": {\"from\": \"comments\", \"op\": \"sum\", \"field\": \"score\"}}"))
            .ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/notes/fields/score_total/rollup/field");
        error.Message.ShouldBe($"Field 'comments.score' is virtual: {NoColumnTail}");
    }

    [Fact]
    public void A_virtual_field_in_events_exclude_is_refused()
    {
        var error = VirtualErrors(Notes(Virtual, events: "{\"exclude\": [\"display\"]}")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/notes/events/exclude");
        error.Message.ShouldBe($"Field 'notes.display' is virtual: {NoColumnTail}");
    }

    [Fact]
    public void A_virtual_field_on_a_dynamic_entity_names_41()
    {
        var descriptor = AlvoDescriptor.Parse(Notes(Virtual, storage: "dynamic"));

        var error = VirtualFieldCheck.Errors(descriptor).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/notes/fields/display/virtual");
        error.Message.ShouldBe(
            "Field 'notes.display' is virtual and 'notes' declares 'storage: dynamic' — dynamic entities hold no virtual field until #41.");
    }

    [Theory]
    [InlineData("beforeCreate")]
    [InlineData("beforeUpdate")]
    public void A_mutate_targeting_a_virtual_field_is_refused_at_apply(string hook)
    {
        var json = Notes(Virtual).Replace(
            "\"rules\": {\"list\"",
            $"\"hooks\": {{\"{hook}\": [{{\"action\": {{\"mutate\": {{\"display\": \"'x'\"}}}}}}]}}, \"rules\": {{\"list\"",
            StringComparison.Ordinal);

        var error = VirtualErrors(json).ShouldHaveSingleItem();

        error.Path.ShouldBe($"/entities/notes/hooks/{hook}/0/action/mutate/display");
        error.Message.ShouldBe($"Field 'notes.display' is virtual: {NoColumnTail}");
        error.FixSuggestion.ShouldBe("Remove 'display' from the mutate, or compute the value in the enricher.");
    }

    [Fact]
    public void A_plain_virtual_string_field_is_accepted_and_maps_IsVirtual()
    {
        var json = Notes(Virtual);

        new DescriptorValidator().Validate(json).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error).ShouldBeEmpty();
        var notes = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(json)).Entities.Single(entity => entity.Name == "notes");
        notes.Fields.Single(field => field.Name == "display").IsVirtual.ShouldBeTrue();
        notes.Fields.Single(field => field.Name == "title").IsVirtual.ShouldBeFalse();
    }

    private const string Virtual = "{\"type\": \"string\", \"virtual\": true}";

    private const string Rules = """{"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}""";

    private static List<DescriptorValidationError> VirtualErrors(string json) =>
        [.. new DescriptorValidator().Validate(json).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)
            .Where(error => error.Message.Contains(NoColumnTail, StringComparison.Ordinal))];

    private static string Notes(
        string display, string? indexes = null, string? events = null, string? storage = null,
        string? commentScore = null, string? noteRollup = null)
    {
        var noteExtras = (indexes is null ? "" : $", \"indexes\": {indexes}")
            + (events is null ? "" : $", \"events\": {events}")
            + (storage is null ? "" : $", \"storage\": \"{storage}\"");
        var rollupField = noteRollup is null ? "" : $", \"score_total\": {noteRollup}";
        var scoreField = commentScore is null ? "" : $", \"score\": {commentScore}";
        return "{\"$schema\": \"https://alvo.dev/schema/v1/project.json\", \"apiVersion\": \"alvo.dev/v1\", \"name\": \"virtuals\", \"entities\": {"
            + $"\"notes\": {{\"fields\": {{\"title\": {{\"type\": \"string\"}}, \"display\": {display}{rollupField}}}, \"rules\": {Rules}{noteExtras}}}, "
            + $"\"comments\": {{\"fields\": {{\"note_id\": {{\"type\": \"ref\", \"entity\": \"notes\"}}{scoreField}}}, \"rules\": {Rules}}}}}}}";
    }
}
