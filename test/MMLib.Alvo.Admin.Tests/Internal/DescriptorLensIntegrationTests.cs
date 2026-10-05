using MMLib.Alvo.Admin.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>What the pickers and Integrations read off the working copy (spec §4.4, §4.7).</summary>
public class DescriptorLensIntegrationTests
{
    private const string Descriptor = """
        {
          "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/hooks/rentals", "secretRef": "rental-desk-signing-key" },
            "billing": { "url": "https://billing.example/hooks", "secretRef": "billing-key", "description": "Invoices" } } },
          "templates": {
            "order-ready": { "subject": "Ready {{new.order_number}}", "body": "Hello" },
            "legacy": { "bodyFile": "legacy.md" } },
          "entities": {
            "rentals": { "fields": {}, "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "rental-desk" } } ] } },
            "service_orders": { "fields": {}, "hooks": {
              "beforeUpdate": [ { "action": { "reject": "no" } } ],
              "afterUpdate": [
                { "action": { "type": "email", "template": "order-ready", "to": "{{new.contact_email}}" } },
                { "action": { "type": "webhook", "endpoint": "billing" } } ] } } }
        }
        """;

    [Fact]
    public void Endpoints_are_read_by_name_in_the_descriptors_order_and_outlive_the_parse()
    {
        var endpoints = DescriptorLens.Endpoints(Descriptor);

        endpoints.Select(endpoint => endpoint.Key).ShouldBe(["rental-desk", "billing"]);
        endpoints[0].Value.GetProperty("secretRef").GetString().ShouldBe("rental-desk-signing-key");
        endpoints[1].Value.GetProperty("url").GetString().ShouldBe("https://billing.example/hooks");
    }

    [Fact]
    public void Templates_are_read_by_name_a_body_file_one_included()
        => DescriptorLens.Templates(Descriptor).Select(template => template.Key).ShouldBe(["order-ready", "legacy"]);

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"webhooks": []}""")]
    [InlineData("""{"webhooks": {"endpoints": "x"}}""")]
    [InlineData("""{"templates": 3}""")]
    public void A_missing_or_malformed_block_reads_as_none(string json)
    {
        DescriptorLens.Endpoints(json).ShouldBeEmpty();
        DescriptorLens.Templates(json).ShouldBeEmpty();
        DescriptorLens.IntegrationUses(json).ShouldBeEmpty();
    }

    [Fact]
    public void Every_hook_that_posts_or_sends_is_a_use_with_its_place()
        => DescriptorLens.IntegrationUses(Descriptor).ShouldBe(
        [
            new DescriptorLens.IntegrationUse("endpoint", "rental-desk", "rentals", "afterCreate", 0),
            new DescriptorLens.IntegrationUse("template", "order-ready", "service_orders", "afterUpdate", 0),
            new DescriptorLens.IntegrationUse("endpoint", "billing", "service_orders", "afterUpdate", 1),
        ]);

    [Theory]
    [InlineData("""{ "bodyFile": "legacy.md" }""", true)]
    [InlineData("""{ "subject": "s", "body": "b" }""", false)]
    [InlineData("""[ "bodyFile" ]""", false)]
    [InlineData("\"bodyFile\"", false)]
    public void A_template_reads_a_body_file_only_when_it_declares_one(string template, bool expected)
    {
        using var document = JsonDocument.Parse(template);

        DescriptorLens.HasBodyFile(document.RootElement).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{ "subject": "Ready" }""", "Ready")]
    [InlineData("""{ "subject": 3 }""", null)]
    [InlineData("""{ "body": "b" }""", null)]
    [InlineData("""[ "subject" ]""", null)]
    public void A_text_property_reads_only_when_it_is_a_string(string owner, string? expected)
    {
        using var document = JsonDocument.Parse(owner);

        DescriptorLens.TextOf(document.RootElement, "subject").ShouldBe(expected);
    }
}
