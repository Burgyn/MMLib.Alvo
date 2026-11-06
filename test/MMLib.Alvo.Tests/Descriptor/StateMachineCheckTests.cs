using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A state machine guards one stored, visible enum field over its declared values; a machine the write path could not
/// honour is a structured refusal at <c>/entities/&lt;e&gt;/stateMachine/…</c> when the descriptor is saved.
/// </summary>
public sealed class StateMachineCheckTests
{
    private const string Pointer = "/entities/bookings/stateMachine";

    private const string Confirm = """ "confirm": {"from": ["draft"], "to": "confirmed"} """;

    [Fact]
    public void A_valid_state_machine_has_no_findings()
    {
        Validate(Machine()).ShouldBeEmpty();
        Validate(Machine(initial: "draft", transitions: Confirm + """, "reopen": {"from": ["*"], "to": "draft", "roles": ["admin"]}""")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("note", "is not an enum")]
    [InlineData("rating", "is computed")]
    [InlineData("missing", "does not declare")]
    public void A_state_machine_over_a_field_that_is_not_a_stored_enum_is_refused(string field, string reason)
    {
        var error = Check(Machine(field: field)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/field");
        error.Message.ShouldContain(reason);
        error.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_transition_naming_an_undeclared_state_is_refused()
    {
        var errors = Check(Machine(transitions: """ "t": {"from": ["draft", "archived"], "to": "closed"} """));

        errors.Select(error => error.Path).ShouldBe([Pointer + "/transitions/t/from/1", Pointer + "/transitions/t/to"], ignoreOrder: true);
        errors.ShouldAllBe(error => error.FixSuggestion!.Contains("'draft', 'confirmed', 'cancelled'"));
    }

    [Fact]
    public void An_initial_state_outside_the_values_or_unlike_the_default_is_refused()
    {
        var outside = Check(Machine(initial: "pending")).ShouldHaveSingleItem();
        outside.Path.ShouldBe(Pointer + "/initial");
        outside.Message.ShouldContain("'pending'");

        var unlike = Check(Machine(initial: "confirmed", field: "defaulted")).ShouldHaveSingleItem();
        unlike.Path.ShouldBe(Pointer + "/initial");
        unlike.Message.ShouldContain("default");
        unlike.FixSuggestion.ShouldNotBeNull().ShouldContain("'draft'");

        Check(Machine(initial: "draft", field: "defaulted")).ShouldBeEmpty();
    }

    [Fact]
    public void A_wildcard_source_beside_other_sources_is_refused()
    {
        var error = Check(Machine(transitions: """ "t": {"from": ["*", "draft"], "to": "cancelled"} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/transitions/t/from");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("\"*\"");
    }

    [Fact]
    public void A_transition_into_a_state_it_leaves_is_refused()
    {
        var error = Check(Machine(transitions: """ "t": {"from": ["draft", "confirmed"], "to": "confirmed"} """)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/transitions/t/to");
        error.Message.ShouldContain("'confirmed'");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("Remove 'confirmed' from 'from'");
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("guarded")]
    public void A_state_machine_over_a_hidden_field_is_refused(string field)
    {
        var error = Check(Machine(field: field)).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer + "/field");
        error.Message.ShouldContain("hidden");
    }

    [Fact]
    public void A_state_machine_or_timers_on_a_dynamic_entity_is_refused()
    {
        var errors = Validate(Machine() + """, "timers": {"reminder": {"at": "starts_at"}}""", storage: "dynamic");

        errors.Select(error => error.Path).ShouldBe([Pointer, "/entities/bookings/timers"], ignoreOrder: true);
        errors.ShouldAllBe(error => error.Message.Contains("storage: dynamic") && error.FixSuggestion!.Contains("physical entity"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = Check(Machine(
            initial: "pending",
            transitions: """ "a": {"from": ["*", "draft"], "to": "nowhere"}, "b": {"from": ["confirmed"], "to": "confirmed"} """));

        errors.Select(error => error.Path).ShouldBe(
            [Pointer + "/initial", Pointer + "/transitions/a/from", Pointer + "/transitions/a/to", Pointer + "/transitions/b/to"],
            ignoreOrder: true);
    }

    private static string Machine(string field = "status", string? initial = null, string transitions = Confirm) =>
        $"\"stateMachine\": {{\"field\": \"{field}\"{(initial is null ? "" : $", \"initial\": \"{initial}\"")}, \"transitions\": {{{transitions}}}}}";

    private static IReadOnlyList<DescriptorValidationError> Check(string members) =>
        StateMachineCheck.Check(AlvoDescriptor.Parse(Json(members, "physical", Computed)));

    private static IReadOnlyList<DescriptorValidationError> Validate(string members, string storage = "physical") =>
        [.. new DescriptorValidator().Validate(Json(members, storage, string.Empty)).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private const string Computed = """, "rating": {"type": "enum", "values": ["draft", "confirmed", "cancelled"], "computed": "status"}""";

    private static string Json(string members, string storage, string computed)
    {
        const string States = """["draft", "confirmed", "cancelled"]""";
        const string Fields =
            $$"""{"status": {"type": "enum", "values": {{States}}, "required": true}, "note": {"type": "string"},"""
            + $$""" "defaulted": {"type": "enum", "values": {{States}}, "default": "draft"},"""
            + $$""" "secret": {"type": "enum", "values": {{States}}, "hidden": true},"""
            + $$""" "guarded": {"type": "enum", "values": {{States}}, "hidden": "!('manager' in @user.roles)"},"""
            + """ "starts_at": {"type": "datetime"}""";
        return """{"$schema": "https://alvo.dev/schema/v1/project.json", "apiVersion": "alvo.dev/v1", "name": "machines", "auth": {"roles": ["manager"]},"""
            + $$""" "entities": {"bookings": {"storage": "{{storage}}", "fields": {{Fields}}{{computed}}}, {{members}},"""
            + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""";
    }
}
