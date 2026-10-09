using MMLib.Alvo.DocsGen.Exchanges;

namespace MMLib.Alvo.DocsGen.Tests.Exchanges;

public class PlaceholdersTests
{
    private static readonly CapturedStep _created = new(
        new CapturedRequest("POST", "/api/tickets", [], "{}"),
        new CapturedResponse(201, "Created", [new("ETag", "\"r1\"")], """{ "id": "abc", "items": [ { "id": "x" } ] }"""));

    [Fact]
    public void A_body_path_and_a_header_resolve() =>
        Placeholders.Resolve("/api/tickets/{0.body.id}?v={0.header.ETag}&i={0.body.items.0.id}", [_created])
            .ShouldBe("/api/tickets/abc?v=\"r1\"&i=x");

    [Fact]
    public void An_unresolvable_placeholder_throws() =>
        Should.Throw<InvalidOperationException>(() => Placeholders.Resolve("/api/{0.body.missing}", [_created]))
            .Message.ShouldContain("{0.body.missing}");

    [Fact]
    public void A_placeholder_naming_a_later_step_throws() =>
        Should.Throw<InvalidOperationException>(() => Placeholders.Resolve("/api/{1.body.id}", [_created]))
            .Message.ShouldContain("{1.body.id}");

    [Fact]
    public void A_header_the_step_does_not_render_throws() =>
        Should.Throw<InvalidOperationException>(() => Placeholders.Resolve("{0.header.Location}", [_created]))
            .Message.ShouldContain("showHeaders");
}
