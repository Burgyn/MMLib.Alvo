using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Typed registrations (spec 2026-10-14 §4.2, D1, D2): the untyped core's registration, with a translated condition and typed messages.</summary>
public sealed class TypedRegistrationTests
{
    [Fact]
    public void A_typed_handler_registers_its_translated_condition_its_entity_and_its_message()
    {
        var handler = Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
            "typed-resolved", when: e => e.Changed(x => x.Status) && e.New.Status == TicketStatus.Resolved))).Handlers.ShouldHaveSingleItem();

        handler.When.ShouldBe("changed(status) && new.status == 'resolved'");
        handler.Source.ShouldBe(new EntityEvents(["tickets"], HandlerVerb.Updated));
        handler.Invoker.MessageType.ShouldBe(typeof(AlvoUpdated<TypedTicket>));
        handler.Typed.ShouldNotBeNull().Shape.Type.ShouldBe(typeof(TypedTicket));
    }

    [Fact]
    public void Typed_and_untyped_registrations_of_one_handler_describe_the_same_condition()
    {
        var registry = Registry(alvo => alvo
            .Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
                "typed", when: e => e.Changed(x => x.Status) && e.New.Status == TicketStatus.Resolved))
            .Entity("tickets", tickets => tickets.OnUpdated("untyped", OnUpdated, when: "changed(status) && new.status == 'resolved'")));

        var items = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions())).Catalog().Items;

        items.Single(item => item.Id == "typed").When.ShouldBe(items.Single(item => item.Id == "untyped").When);
    }

    [Fact]
    public void A_typed_entity_takes_a_cel_string_too() =>
        Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnCreated(
            "typed-cel", (AlvoCreated<TypedTicket> _) => Task.CompletedTask, when: "new.title == 'x'"))).Handlers.ShouldHaveSingleItem().When.ShouldBe("new.title == 'x'");

    [Fact]
    public void A_type_without_an_entity_attribute_is_refused_at_registration()
    {
        var refusal = Should.Throw<ArgumentException>(() => Registry(alvo => alvo.Entity<Unmarked>(_ => { })));

        refusal.Message.ShouldContain("[AlvoEntity(");
        refusal.ParamName.ShouldBe("T");
    }

    [Fact]
    public void A_type_mapped_to_every_entity_is_refused_at_registration()
    {
        var refusal = Should.Throw<ArgumentException>(() => Registry(alvo => alvo.Entity<EveryEntity>(_ => { })));

        refusal.Message.ShouldContain("EveryEntity");
        refusal.Message.ShouldContain("Entity(\"*\"");
        refusal.ParamName.ShouldBe("T");
    }

    [Fact]
    public void An_untranslatable_condition_is_refused_at_registration_naming_the_handler() =>
        Should.Throw<AlvoTranslationException>(() => Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
            "typed-bad", when: e => e.New.Title.GetHashCode() == 1)))).Message.ShouldContain("in extension 'typed-bad' (when)");

    [Fact]
    public void A_typed_application_event_handler_reads_its_type_from_the_attribute()
    {
        var handler = Registry(alvo => alvo.AddEventHandler<TicketEscalated, EscalationHandler>("escalate")).Handlers.ShouldHaveSingleItem();

        handler.Source.ShouldBe(new NamedEvent("desk.escalated"));
        handler.Typed.ShouldNotBeNull();
    }

    [Fact]
    public void A_payload_type_without_an_event_attribute_is_refused_at_registration()
    {
        var refusal = Should.Throw<ArgumentException>(() => Registry(alvo => alvo.AddEventHandler<TypedTicket, UnusedHandler>("no-event")));

        refusal.Message.ShouldContain("[AlvoEvent(");
        refusal.ParamName.ShouldBe("TMessage");
    }

    [Fact]
    public void The_typed_message_is_built_from_the_event_images()
    {
        var handler = Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>("typed"))).Handlers.ShouldHaveSingleItem();
        var data = new AlvoEventData { OldRecord = Ticket("open"), Record = Ticket("resolved") };

        var message = handler.Typed!.Message(data).ShouldBeOfType<AlvoUpdated<TypedTicket>>();

        (message.Old.Status, message.New.Status).ShouldBe((TicketStatus.Open, TicketStatus.Resolved));
    }

    [Fact]
    public void A_typed_payload_is_built_from_the_event_data() =>
        Registry(alvo => alvo.AddEventHandler<TicketEscalated, EscalationHandler>("escalate")).Handlers.ShouldHaveSingleItem()
            .Typed!.Message(new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["reason"] = "late", ["level"] = 2L }) })
            .ShouldBe(new TicketEscalated("late", 2));

    [Fact]
    public void A_delivery_hands_a_typed_handler_its_typed_message_and_an_untyped_one_the_record()
    {
        var registry = Registry(alvo => alvo
            .Entity<TypedTicket>(tickets => tickets.OnCreated("typed", (AlvoCreated<TypedTicket> _) => Task.CompletedTask))
            .Entity("tickets", tickets => tickets.OnCreated("untyped", (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)));
        var created = Created(Ticket("open"));

        HandlerMessage.For(created, registry.Handlers.Single(handler => handler.Id == "typed"))
            .ShouldBeOfType<AlvoCreated<TypedTicket>>().New.Title.ShouldBe("Printer jam");
        HandlerMessage.For(created, registry.Handlers.Single(handler => handler.Id == "untyped"))
            .ShouldBeOfType<AlvoCreated<AlvoRecord>>().New.ShouldBeSameAs(created.Data.Record);
    }

    private static AlvoEvent Created(AlvoRecord record) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UnixEpoch),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = DateTimeOffset.UnixEpoch,
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.System,
        CorrelationId = "corr-1",
        Data = new AlvoEventData { Record = record },
    };

    private static AlvoRecord Ticket(string status) => new(new Dictionary<string, object?>
    {
        ["id"] = "7d1a3c38-9f0b-4c39-9c51-5f2a8d0e6b21",
        ["title"] = "Printer jam",
        ["code"] = null,
        ["status"] = status,
    });

    private sealed class UnusedHandler : IAlvoEventHandler<TypedTicket>
    {
        public Task HandleAsync(TypedTicket message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
