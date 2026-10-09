using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Declaring an endpoint or a template in the working copy (spec §5.5).</summary>
public class WorkingCopyIntegrationTests
{
    [Fact]
    public void A_new_endpoint_creates_its_blocks_and_is_written_in_the_schemas_order()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");

        copy.DeclareEndpoint("billing", "https://billing.example/hooks", "billing-signing-key", "Invoices", editing: false).ShouldBeTrue();

        Node(copy)["webhooks"]!["endpoints"]!["billing"]!.ToJsonString()
            .ShouldBe("""{"url":"https://billing.example/hooks","secretRef":"billing-signing-key","description":"Invoices"}""");
    }

    [Fact]
    public void A_new_endpoint_with_a_name_already_declared_is_refused_and_changes_nothing()
    {
        var copy = Copy(WithEndpoint);
        var before = copy.Json;

        copy.DeclareEndpoint("rental-desk", "https://x.example", "k", null, editing: false).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void An_edit_patches_the_endpoint_in_place_and_keeps_what_it_does_not_draw()
    {
        var copy = Copy(WithEndpoint);

        copy.DeclareEndpoint("rental-desk", "https://desk.example/h", "desk-key", null, editing: true).ShouldBeTrue();

        Node(copy)["webhooks"]!["endpoints"]!["rental-desk"]!.ToJsonString()
            .ShouldBe("""{"url":"https://desk.example/h","secretRef":"desk-key","x-owner":"ops"}""");
    }

    [Fact]
    public void Editing_an_endpoint_nothing_declares_is_refused_and_creates_nothing()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");
        var before = copy.Json;

        copy.DeclareEndpoint("ghost", "https://x.example", "k", null, editing: true).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Theory]
    [InlineData("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "webhooks": [] }""")]
    [InlineData("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "webhooks": { "endpoints": "x" } }""")]
    public void A_block_of_the_wrong_shape_is_left_for_the_apply_to_refuse(string json)
    {
        var copy = Copy(json);
        var before = copy.Json;

        copy.DeclareEndpoint("billing", "https://x.example", "k", null, editing: false).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void A_new_template_is_subject_then_body_and_a_blank_subject_is_left_out()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");

        copy.DeclareTemplate("ready", "Ready {{new.order_number}}", "Hello", editing: false).ShouldBeTrue();
        copy.DeclareTemplate("plain", "  ", "Hello", editing: false).ShouldBeTrue();

        Node(copy)["templates"]!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"ready":{"subject":"Ready {{new.order_number}}","body":"Hello"},"plain":{"body":"Hello"}}""");
    }

    [Fact]
    public void A_template_that_reads_a_body_file_is_not_edited()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "templates": { "legacy": { "bodyFile": "a.md" } } }""");
        var before = copy.Json;

        copy.DeclareTemplate("legacy", null, "Inline now", editing: true).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void An_edit_acts_only_on_the_endpoint_it_drew_and_a_url_changed_meanwhile_is_kept()
    {
        var copy = Copy(WithEndpoint);
        var drawn = copy.EndpointAsDrawn("rental-desk");
        copy.DeclareEndpoint("rental-desk", "https://new.example/hook", "rental-desk-signing-key", null, editing: true).ShouldBeTrue();
        var overtaken = copy.Json;

        copy.DeclareEndpoint("rental-desk", "http://127.0.0.1:5081/h", "rental-desk-signing-key", "Only the description", editing: true, drawn)
            .ShouldBeFalse();

        copy.Json.ShouldBe(overtaken);
        Node(copy)["webhooks"]!["endpoints"]!["rental-desk"]!["url"]!.GetValue<string>().ShouldBe("https://new.example/hook");
    }

    [Fact]
    public void An_edit_of_the_endpoint_as_it_was_drawn_is_written()
    {
        var copy = Copy(WithEndpoint);

        copy.DeclareEndpoint("rental-desk", "http://127.0.0.1:5081/h", "rental-desk-signing-key", "Desk", editing: true,
            copy.EndpointAsDrawn("rental-desk")).ShouldBeTrue();

        Node(copy)["webhooks"]!["endpoints"]!["rental-desk"]!["description"]!.GetValue<string>().ShouldBe("Desk");
    }

    [Fact]
    public void An_edit_acts_only_on_the_template_it_drew()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "templates": { "ready": { "body": "Hello" } } }""");
        var drawn = copy.TemplateAsDrawn("ready");
        copy.DeclareTemplate("ready", null, "Changed elsewhere", editing: true).ShouldBeTrue();
        var overtaken = copy.Json;

        copy.DeclareTemplate("ready", "A subject", "Hello", editing: true, drawn).ShouldBeFalse();
        copy.Json.ShouldBe(overtaken);

        copy.DeclareTemplate("ready", "A subject", "Changed elsewhere", editing: true, copy.TemplateAsDrawn("ready")).ShouldBeTrue();
    }

    [Fact]
    public void Nothing_declared_draws_as_nothing()
    {
        var copy = Copy(WithEndpoint);

        copy.EndpointAsDrawn("ghost").ShouldBeNull();
        copy.TemplateAsDrawn("ghost").ShouldBeNull();
    }

    [Fact]
    public void A_declaration_is_a_pending_edit()
    {
        var copy = Copy(WithEndpoint);
        copy.PendingCount.ShouldBe(0);

        copy.DeclareTemplate("ready", null, "Hello", editing: false).ShouldBeTrue();

        copy.PendingCount.ShouldBe(1);
    }

    private const string WithEndpoint = """
        { "apiVersion": "alvo.dev/v1", "name": "x", "entities": {},
          "webhooks": { "endpoints": { "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key", "x-owner": "ops" } } } }
        """;

    private static WorkingCopy Copy(string json)
    {
        var copy = new WorkingCopy();
        copy.Take(json, revision: 1);
        return copy;
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;
}
