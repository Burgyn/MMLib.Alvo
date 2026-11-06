using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;

using System.Text.Encodings.Web;
using System.Text.Json;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The extension catalog (spec 2026-10-14 §4.13, HX-04 acceptance (1)): registrations as metadata, in order.</summary>
public sealed class ExtensionCatalogTests
{
    /// <summary>The wire form the Management API serves, relaxed-escaped so the snapshot reads as the CEL it carries.</summary>
    private static readonly JsonSerializerOptions _wire = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public Task The_catalog_lists_entity_every_entity_and_application_handlers_in_order() =>
        Verify(JsonSerializer.Serialize(Catalog(Registered()).Catalog(), _wire));

    [Fact]
    public void The_catalog_is_ordered_by_order_and_then_by_registration()
    {
        var registry = Registry(alvo => alvo
            .Entity("tickets", tickets => tickets
                .OnCreated("late", OnCreated, configure: options => options.Order = 5)
                .OnUpdated("second", OnUpdated)
                .OnDeleted("first", OnDeleted, configure: options => options.Order = -2)
                .OnSaved("third", OnSaved)));

        Catalog(registry).Items.Select(item => item.Id).ShouldBe(["first", "second", "third", "late"]);
    }

    [Fact]
    public void A_retired_id_is_not_an_extension()
        => Catalog(Registered()).Items.Select(item => item.Id).ShouldNotContain("old-sync");

    [Fact]
    public void A_class_handler_is_named_only_when_the_host_asks()
    {
        var registry = Registry(alvo => alvo
            .Entity("tickets", t => t.OnUpdated<Resolved>("by-class"))
            .Entity("notes", n => n.OnCreated("by-delegate", OnCreated)));

        Catalog(registry).Items.ShouldAllBe(item => item.TypeName == null);
        var named = Catalog(registry, exposeTypeNames: true).Items;
        named.Single(item => item.Id == "by-class").TypeName.ShouldBe(typeof(Resolved).FullName);
        named.Single(item => item.Id == "by-delegate").TypeName.ShouldBeNull("a delegate has no type worth naming");
    }

    [Fact]
    public void The_simulator_filter_keeps_what_runs_on_the_entity_and_operation()
    {
        var catalog = Catalog(Registered());

        catalog.RunningOn("tickets", "update").Select(item => item.Id).ShouldBe(["audit-every-save", "ticket-resolved"]);
        catalog.RunningOn("notes", "create").Select(item => item.Id).ShouldBe(["audit-every-save"]);
        catalog.RunningOn("tickets", "list").ShouldBeEmpty("no extension runs on a read in this build");
        catalog.RunningOn("tickets", "delete").ShouldBeEmpty();
    }

    [Fact]
    public void The_honoured_kinds_are_named_once_each_and_only_when_registered()
    {
        Catalog(Registered()).HonouredKinds.ShouldBe(["extensions.eventHandler"]);
        Catalog(ExtensionRegistry.Empty).HonouredKinds.ShouldBeEmpty();
    }

    [Fact]
    public void A_narrowing_is_catalogued_as_a_read_on_list_and_get()
    {
        var catalog = Catalog(Registry(alvo => alvo.Entity("tickets", t => t.Narrow("open-only", (MMLib.Alvo.Reads.QueryNarrowing _) => null))));

        var item = catalog.Items.ShouldHaveSingleItem();
        (item.Kind, item.Stage, item.When, item.Entities.Single()).ShouldBe((AlvoExtensionKind.Narrowing, AlvoExtensionStage.Read, (string?)null, "tickets"));
        item.Operations.ShouldBe(["list", "get"]);
        catalog.HonouredKinds.ShouldContain("extensions.narrowing");
        catalog.RunningOn("tickets", "list").ShouldHaveSingleItem().Id.ShouldBe("open-only");
        catalog.RunningOn("tickets", "create").ShouldBeEmpty();
    }

    [Fact]
    public void A_kind_s_wire_name_is_its_serialised_form()
        => ExtensionCatalog.WireName(AlvoExtensionKind.EventHandler).ShouldBe("eventHandler");

    private static ExtensionRegistry Registered() => Registry(alvo => alvo
        .Entity("tickets", tickets => tickets.OnUpdated(
            "ticket-resolved",
            OnUpdated,
            when: "changed(status) && new.status == 'resolved'",
            configure: options => options.Description = "Posts to the chat when a ticket is resolved."))
        .Entity("*", every => every.OnSaved(
            "audit-every-save",
            OnSaved,
            configure: options =>
            {
                options.Order = -1;
                options.Timeout = TimeSpan.FromSeconds(5);
            }))
        .AddEventHandler(
            "orders-approved",
            "orders.approved",
            OnRecord,
            configure: options =>
            {
                options.RunAs = AlvoRunAs.EventActor;
                options.Ordering = AlvoOrdering.None;
            })
        .AddRetiredExtension("old-sync", AlvoRetirement.Discard));

    [Fact]
    public void A_mapped_command_is_listed_after_the_handlers_with_its_route_method_and_access()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.Entity("tickets", tickets => tickets.OnCreated("on-ticket", OnCreated)));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MMLib.Alvo.Commands.Internal.CommandRegistry>().Map(
            "resolve_ticket", "/commands/tickets/{id:guid}/resolve", "tickets", MMLib.Alvo.Commands.Internal.CommandAccess.Of("'staff' in @user.roles"));

        var items = Catalog(provider.GetRequiredService<ExtensionRegistry>()).Items;

        items.Select(item => item.Id).ShouldBe(["on-ticket", "resolve_ticket"]);
        var command = items[1];
        command.Kind.ShouldBe(AlvoExtensionKind.Command);
        command.Stage.ShouldBe(AlvoExtensionStage.Http);
        command.Entities.ShouldBe(["tickets"]);
        command.Operations.ShouldBeEmpty();
        command.Route.ShouldBe("/commands/tickets/{id:guid}/resolve");
        command.HttpMethod.ShouldBe("POST");
        command.Access.ShouldBe("'staff' in @user.roles");
    }

    [Fact]
    public void An_internal_command_runs_before_any_transaction_and_has_no_route()
    {
        var catalog = Catalog(Registry(alvo => alvo.AddCommand<Escalate>("escalate_ticket", "true")));
        var item = catalog.Items.ShouldHaveSingleItem();

        item.Stage.ShouldBe(AlvoExtensionStage.PreTransaction);
        item.Route.ShouldBeNull();
        item.HttpMethod.ShouldBeNull();
        catalog.HonouredKinds.ShouldBe(["extensions.command"]);
    }

    private sealed class Escalate : MMLib.Alvo.Commands.IAlvoCommand<Guid>
    {
        public Task HandleAsync(Guid input, MMLib.Alvo.Commands.AlvoCommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ExtensionCatalog Catalog(ExtensionRegistry registry, bool exposeTypeNames = false) =>
        new(registry, Options.Create(new AlvoExtensionOptions { ExposeTypeNames = exposeTypeNames }));

    private sealed class Resolved : IAlvoEventHandler<AlvoUpdated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoUpdated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
