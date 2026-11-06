using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A timer fires a custom event a host handler consumes, so the event it fires — declared, or the default
/// <c>&lt;entity&gt;.&lt;timer&gt;</c> — must be a name a host may mint, refused at apply otherwise.
/// </summary>
public sealed class EntityTimerCheckTests
{
    [Fact]
    public void A_timer_with_its_own_event_name_has_no_findings()
    {
        Check("bookings", "reminder", """{"at": "starts_at"}""").ShouldBeEmpty();
        Check("entity", "x", """{"at": "starts_at", "event": "bookings.reminder"}""").ShouldBeEmpty();
    }

    [Fact]
    public void A_default_event_in_a_reserved_namespace_is_refused()
    {
        var error = Check("entity", "x", """{"at": "starts_at"}""").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/entity/timers/x");
        error.Message.ShouldContain("'entity.x'");
        error.Message.ShouldContain("reserved");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("Declare 'event'");
    }

    [Fact]
    public void An_event_in_the_alvo_namespace_is_refused()
    {
        var error = Check("bookings", "reminder", """{"at": "starts_at", "event": "alvo.reminder"}""").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/bookings/timers/reminder/event");
        error.Message.ShouldContain("'alvo.reminder'");
    }

    [Fact]
    public void The_default_event_maps_dashes_to_underscores()
    {
        EntityTimerCheck.EventOf("work-orders", "visit-reminder", new EntityTimer { At = "starts_at" }).ShouldBe("work_orders.visit_reminder");
        EntityTimerCheck.EventOf("bookings", "reminder", new EntityTimer { At = "starts_at", Event = "bookings.due" }).ShouldBe("bookings.due");
    }

    [Fact]
    public void Timers_on_an_entity_a_cascade_delete_reaches_are_refused()
    {
        var descriptor = AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "timers", "entities": {"""
            + """ "customers": {"fields": {"name": {"type": "string"}}},"""
            + """ "bookings": {"fields": {"starts_at": {"type": "datetime"}, "customer": {"type": "ref", "entity": "customers", "onDelete": "cascade"}},"""
            + """ "timers": {"reminder": {"at": "starts_at"}}}}}""");

        var error = EntityTimerCheck.Check(descriptor).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/bookings/timers");
        error.Message.ShouldContain("'customer'");
        error.Message.ShouldContain("cascade");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("restrict");
    }

    [Theory]
    [InlineData("bookings.reminder")]
    [InlineData("billing.invoice.settled")]
    [InlineData("a1_b.c_2")]
    [InlineData("bookings")]
    [InlineData("Bookings.reminder")]
    [InlineData("bookings.reminder-due")]
    [InlineData("bookings..reminder")]
    [InlineData("bookings.1st")]
    [InlineData("bookings.reminder.")]
    public void The_schema_s_event_pattern_agrees_with_the_event_name_grammar(string type)
    {
        var pattern = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!
            ["$defs"]!["entity"]!["properties"]!["timers"]!["additionalProperties"]!["properties"]!["event"]!["pattern"]!.GetValue<string>();

        Regex.IsMatch(type, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).ShouldBe(AlvoEventName.IsWellFormed(type));
    }

    private static IReadOnlyList<DescriptorValidationError> Check(string entity, string timer, string declaration) =>
        EntityTimerCheck.Check(AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "timers", "entities": {"""
            + $"\"{entity}\": "
            + """{"fields": {"starts_at": {"type": "datetime"}}, "timers": {"""
            + $"\"{timer}\": {declaration}"
            + "}}}}"));
}
