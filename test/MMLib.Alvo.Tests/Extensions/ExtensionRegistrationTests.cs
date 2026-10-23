using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The registration verbs (spec 2026-10-14 §4.2, X14): each call adds one internal registration; shape errors a compiler
/// cannot represent are refused at the call, everything that needs the schema is refused at boot (Task 7).
/// </summary>
public sealed class ExtensionRegistrationTests
{
    [Fact]
    public void An_entity_handler_is_one_registration_bound_to_its_entity_verb_and_condition()
    {
        var registry = Registered(alvo => alvo.Entity("tickets", tickets => tickets.OnUpdated(
            "ticket-resolved", (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "new.status == 'resolved'", configure: options => options.Timeout = TimeSpan.FromSeconds(3))));

        var handler = registry.Handlers.ShouldHaveSingleItem();
        handler.Id.ShouldBe("ticket-resolved");
        handler.Source.ShouldBe(new EntityEvents(["tickets"], HandlerVerb.Updated));
        handler.When.ShouldBe("new.status == 'resolved'");
        handler.Settings.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
        handler.Invoker.MessageType.ShouldBe(typeof(AlvoUpdated<AlvoRecord>));
    }

    [Fact]
    public void Entities_binds_one_registration_to_every_named_entity()
    {
        var registry = Registered(alvo => alvo.Entities(["orders", "invoices"], entities => entities.OnCreated<NoOpCreated>("audit")));

        ((EntityEvents)registry.Handlers.ShouldHaveSingleItem().Source).Entities.ShouldBe(["orders", "invoices"]);
    }

    [Fact]
    public void The_star_binds_every_entity() =>
        ((EntityEvents)Registered(alvo => alvo.Entity("*", all => all.OnSaved<NoOpSaved>("audit-all")))
            .Handlers.Single().Source).IsAll.ShouldBeTrue();

    [Fact]
    public void Registrations_keep_the_order_they_were_made_in()
    {
        var registry = Registered(alvo => alvo
            .Entity("tickets", tickets => tickets.OnCreated<NoOpCreated>("first").OnDeleted("second", (AlvoDeleted<AlvoRecord> _) => { }))
            .AddRetiredExtension("old-sync", AlvoRetirement.Discard)
            .AddEventHandler("third", "orders.approved", (AlvoRecord _) => Task.CompletedTask));

        registry.All.Select(registration => registration.Id).ShouldBe(["first", "second", "old-sync", "third"]);
        registry.Handlers.Select(handler => handler.Sequence).ShouldBe([0, 1, 3]);
        registry.Retired.ShouldHaveSingleItem().Retirement.ShouldBe(AlvoRetirement.Discard);
    }

    [Theory]
    [InlineData("orders.approved")]
    [InlineData("alvo.command.issue_invoice.completed")]
    public void An_application_or_framework_event_can_be_handled(string type) =>
        Registered(alvo => alvo.AddEventHandler<NoOpRecord>("handle-it", type)).Handlers.Single().Source.ShouldBe(new NamedEvent(type));

    [Fact]
    public void An_entity_event_is_handled_through_its_entity_not_by_name()
    {
        var refusal = Should.Throw<ArgumentException>(() =>
            Registered(alvo => alvo.AddEventHandler<NoOpRecord>("by-name", "entity.tickets.updated")));

        refusal.Message.ShouldContain("Entity(\"tickets\"");
    }

    [Theory]
    [InlineData("auth.user.login")]
    [InlineData("Orders.Approved")]
    [InlineData("approved")]
    public void A_name_no_event_can_carry_is_refused(string type) =>
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.AddEventHandler<NoOpRecord>("by-name", type)));

    [Fact]
    public void A_blank_id_or_condition_is_refused_at_the_call()
    {
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.Entity("tickets", t => t.OnCreated<NoOpCreated>(" "))));
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.Entity("tickets", t => t.OnCreated<NoOpCreated>("id", when: " "))));
    }

    [Fact]
    public void An_undefined_retirement_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Registered(alvo => alvo.AddRetiredExtension("old", (AlvoRetirement)7)));

    [Theory]
    [InlineData("send-confirmation", true)]
    [InlineData("a_b", false)]
    [InlineData("Send", false)]
    [InlineData("9lives", false)]
    public void An_extension_id_is_lower_case_letters_digits_and_hyphens(string id, bool valid) =>
        ExtensionIds.IsExtensionId(id).ShouldBe(valid);

    [Theory]
    [InlineData("a_b")]
    [InlineData("issue-invoice")]
    [InlineData("billing.issue_invoice")]
    public void A_retired_id_may_be_any_catalog_id(string id) => ExtensionIds.IsRetirableId(id).ShouldBeTrue();

    [Fact]
    public void Hyphens_and_underscores_derive_one_event_name_segment() =>
        ExtensionIds.DerivedSegment("a-b").ShouldBe(ExtensionIds.DerivedSegment("a_b"));

    private static ExtensionRegistry Registered(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ExtensionRegistry>();
    }

    private sealed class NoOpCreated : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpSaved : IAlvoEventHandler<AlvoSaved<AlvoRecord>>
    {
        public Task HandleAsync(AlvoSaved<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpRecord : IAlvoEventHandler<AlvoRecord>
    {
        public Task HandleAsync(AlvoRecord message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
