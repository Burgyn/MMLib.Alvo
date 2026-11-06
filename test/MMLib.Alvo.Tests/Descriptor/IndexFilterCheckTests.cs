using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// An index's <c>where</c> is a stored predicate over the entity's own fields — HX-11's surface minus the clock and temporal
/// arithmetic, refused over a hidden field (D11) — so a predicate no partial index can carry is a structured refusal at
/// <c>/entities/&lt;e&gt;/indexes/&lt;i&gt;/where</c> when the descriptor is saved.
/// </summary>
public sealed class IndexFilterCheckTests
{
    private const string Pointer = "/entities/members/indexes/0/where";

    [Fact]
    public void A_valid_where_has_no_findings()
    {
        Errors("active").ShouldBeEmpty();
        Errors("active && email != 'x'").ShouldBeEmpty();
    }

    /// <summary>
    /// Review R4: a timestamp constant is not a stored predicate's operand in HX-12 — <c>timestamp('…')</c> stays an
    /// in-process function a Rule cannot call, so a <c>where</c> comparing with one is refused when the descriptor is saved.
    /// </summary>
    [Theory]
    [InlineData("joined_at > timestamp('2026-01-01T00:00:00Z')")]
    [InlineData("joined_at > timestamp(email)")]
    public void A_where_calling_timestamp_is_refused(string where) =>
        Errors(where).ShouldHaveSingleItem().Path.ShouldBe(Pointer);

    [Theory]
    [InlineData("joined_at > now()", "now()")]
    [InlineData("joined_at - duration('1h') > joined_at", "duration")]
    [InlineData("score > 0 && duration('1h') > duration('30m')", "duration")]
    public void A_where_reading_now_or_a_duration_is_refused(string where, string named)
    {
        var error = Errors(where).ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldContain(named);
    }

    [Fact]
    public void A_where_reading_the_caller_is_refused()
    {
        var error = Errors("owner == @user.id").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldContain("@user.id");
    }

    [Fact]
    public void A_where_reading_an_undeclared_field_is_refused()
    {
        var error = Errors("missing == 'x'").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldContain("missing");
    }

    [Fact]
    public void A_where_reading_a_hidden_field_is_refused()
    {
        var error = Errors("secret == 'x'").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe(
            "An index of 'members' declares a 'where' that reads a hidden field ('members.secret'): which rows it holds would "
            + "disclose a value its readers cannot see.");
    }

    [Fact]
    public void A_where_on_a_dynamic_entity_is_refused_naming_41()
    {
        var error = Errors("active", storage: "dynamic").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
        error.Message.ShouldBe(
            "An index of 'members' declares 'where', which the database keeps as a partial index, and 'members' declares "
            + "'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.");
        error.FixSuggestion.ShouldBe("Remove 'where', or make 'members' a physical entity.");
    }

    [Fact]
    public void A_where_that_is_not_a_predicate_is_refused()
    {
        var error = Errors("score").ShouldHaveSingleItem();
        error.Path.ShouldBe(Pointer);
    }

    [Fact]
    public void Only_the_index_that_declares_a_bad_where_is_refused()
    {
        var error = Errors("active", secondWhere: "missing == 'x'").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/members/indexes/1/where");
    }

    private static IReadOnlyList<DescriptorValidationError> Errors(string where, string storage = "physical", string? secondWhere = null)
    {
        const string Fields =
            """{"email": {"type": "string"}, "active": {"type": "boolean", "required": true}, "score": {"type": "integer"},"""
            + """ "joined_at": {"type": "datetime"}, "secret": {"type": "string", "hidden": true}, "owner": {"type": "uuid"}}""";
        var second = secondWhere is null ? string.Empty : $$""", {"fields": ["score"], "where": {{Quoted(secondWhere)}}}""";
        var json =
            $$"""{"$schema": "https://alvo.dev/schema/v1/project.json", "apiVersion": "alvo.dev/v1", "name": "indexes", "entities": {"members": {"storage": "{{storage}}", "fields": {{Fields}},"""
            + $$""" "indexes": [{"fields": ["email"], "unique": true, "where": {{Quoted(where)}}}{{second}}],"""
            + """ "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""";

        return [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
    }

    private static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text);
}
