using MMLib.Alvo.Admin.Components.Integrations;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>The rows Integrations lists from the working copy (spec §4.7).</summary>
public class IntegrationRowsTests
{
    private const string Applied = """
        { "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key" },
            "old-desk": { "url": "https://old.example", "secretRef": "old-key" } } },
          "templates": { "order-ready": { "subject": "Ready", "body": "Hello" } },
          "entities": {} }
        """;

    private const string Working = """
        { "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key" },
            "old-desk": { "url": "https://changed.example", "secretRef": "old-key" },
            "billing": { "url": "https://billing.example", "secretRef": "billing-key", "x-owner": "ops" } } },
          "templates": {
            "order-ready": { "subject": "Ready", "body": "Hello" },
            "legacy": { "bodyFile": "legacy.md" } },
          "entities": { "rentals": { "fields": {}, "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "rental-desk" } } ] } } } }
        """;

    [Fact]
    public void An_endpoint_is_staged_when_the_applied_revision_lacks_it_or_holds_it_otherwise()
        => IntegrationRows.Endpoints(Working, Applied).Select(row => (row.Name, row.Staged))
            .ShouldBe([("rental-desk", false), ("old-desk", true), ("billing", true)]);

    [Fact]
    public void An_endpoint_with_a_key_the_sheet_does_not_draw_is_not_drawable()
        => IntegrationRows.Endpoints(Working, Applied).Single(row => row.Name == "billing").Drawable.ShouldBeFalse();

    [Fact]
    public void An_endpoint_carries_the_hooks_that_post_to_it()
    {
        var desk = IntegrationRows.Endpoints(Working, Applied).Single(row => row.Name == "rental-desk");

        desk.Uses.ShouldHaveSingleItem().Entity.ShouldBe("rentals");
        (desk.Url, desk.SecretRef, desk.Drawable).ShouldBe(("http://127.0.0.1:5081/h", "rental-desk-signing-key", true));
    }

    [Fact]
    public void A_template_that_reads_a_body_file_is_flagged_and_not_drawable()
    {
        var legacy = IntegrationRows.Templates(Working, Applied).Single(row => row.Name == "legacy");

        (legacy.HasBodyFile, legacy.Drawable, legacy.Staged).ShouldBe((true, false, true));
    }

    [Fact]
    public void An_applied_template_unchanged_is_not_staged()
        => IntegrationRows.Templates(Working, Applied).Single(row => row.Name == "order-ready").Staged.ShouldBeFalse();

    [Fact]
    public void Text_that_is_not_a_descriptor_lists_nothing()
    {
        IntegrationRows.Endpoints("nope", "{}").ShouldBeEmpty();
        IntegrationRows.Templates("nope", "{}").ShouldBeEmpty();
    }
}
