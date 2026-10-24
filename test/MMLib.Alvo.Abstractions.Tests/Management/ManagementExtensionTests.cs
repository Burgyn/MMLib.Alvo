using MMLib.Alvo.Management;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Abstractions.Tests.Management;

/// <summary>The extension catalog's wire form: camelCase members, string enums, lists that are never null.</summary>
public sealed class ManagementExtensionTests
{
    [Fact]
    public void An_extension_serialises_its_kind_and_stage_as_their_wire_names()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal(), JsonSerializerOptions.Web)!;

        wire["kind"]!.GetValue<string>().ShouldBe("eventHandler");
        wire["stage"]!.GetValue<string>().ShouldBe("postCommit");
    }

    [Fact]
    public void An_extension_naming_no_entity_or_operation_carries_empty_lists_not_nulls()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal(), JsonSerializerOptions.Web)!;

        wire["entities"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
        wire["operations"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
    }

    [Fact]
    public void A_timeout_travels_as_a_time_span_string()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal() with { Timeout = TimeSpan.FromSeconds(30) }, JsonSerializerOptions.Web)!;

        wire["timeout"]!.GetValue<string>().ShouldBe("00:00:30");
    }

    [Fact]
    public void The_catalog_is_an_object_so_it_can_grow()
    {
        var wire = JsonSerializer.SerializeToNode(new ManagementExtensionCatalog { Items = [Minimal()] }, JsonSerializerOptions.Web)!;

        wire["items"].ShouldBeOfType<JsonArray>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_verdict_built_as_before_lists_no_extensions_and_still_equals_its_twin()
    {
        var verdict = new ManagementPolicyVerdict(true, null, "true", null, null, [], []);

        verdict.Extensions.ShouldBeEmpty();
        verdict.ShouldBe(new ManagementPolicyVerdict(true, null, "true", null, null, [], []));
    }

    [Fact]
    public void The_first_members_are_pinned_to_zero_so_appending_never_renumbers_them()
    {
        ((int)AlvoExtensionKind.EventHandler).ShouldBe(0);
        ((int)AlvoExtensionStage.PostCommit).ShouldBe(0);
    }

    private static ManagementExtension Minimal() => new()
    {
        Id = "ticket-resolved",
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
    };
}
