using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// Boot validation of typed mappings (spec 2026-10-14 §4.2, E7; HX-05 acceptance (5)): a property with no field, of a type
/// its field does not hold, reading a field left out of the event images, or an enum member with no declared value refuses
/// boot at <c>extensions/&lt;id&gt;/record</c>, all at once.
/// </summary>
public sealed class TypedBindingTests
{
    [Fact]
    public void A_typed_handler_that_fits_binds()
    {
        var result = Compile(alvo => alvo.Entity<TypedTicket>(t => t.OnUpdated<TypedTicketResolved>("typed", when: e => e.New.Status == TicketStatus.Resolved)));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Handler("typed").ShouldNotBeNull();
    }

    [Fact]
    public void A_property_with_no_field_refuses_boot_naming_the_property() =>
        Problem<TicketWithVat>().Message.ShouldBe("TicketWithVat.VatId maps to 'vat_id', which 'tickets' does not declare.");

    [Fact]
    public void A_mistyped_property_refuses_boot() =>
        Problem<TicketWithNumericCode>().Message.ShouldBe("TicketWithNumericCode.Code is an Int64, but 'code' is a string field, which holds String.");

    [Fact]
    public void A_property_reading_a_field_left_out_of_the_event_images_refuses_boot() =>
        Problem<TicketWithSecret>().Message.ShouldContain("leaves out of its event images (events.exclude)");

    [Fact]
    public void A_property_that_cannot_hold_null_on_an_optional_field_refuses_boot() =>
        Problem<TicketWithStrictCode>().Message.ShouldBe("'code' may be empty, but TicketWithStrictCode.Code cannot hold null.");

    [Fact]
    public void An_enum_member_with_no_declared_value_refuses_boot() =>
        Problem<TicketWithArchive>().Message.ShouldStartWith("ArchivedTicketStatus.Archived maps to 'archived', which is not a value of 'status'");

    [Fact]
    public void A_property_no_field_type_holds_refuses_even_before_a_descriptor()
    {
        var problems = ExtensionBindingCompiler.Check(
            Registry(alvo => alvo.Entity<TicketWithScore>(t => t.OnCreated("scored", (AlvoCreated<TicketWithScore> _) => Task.CompletedTask))),
            CelFixtures.Compiler,
            EveryService()).Problems;

        problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/scored/record");
    }

    /// <summary>
    /// A shape with problems of its own never binds, so no delivery ever reaches it to fail on materializing its record
    /// (the Task 7 review: it used to bind and then fail every attempt until MaxAttempts).
    /// </summary>
    [Fact]
    public void A_shape_with_problems_of_its_own_never_binds_and_is_not_mapped_again()
    {
        var result = Compile(alvo => alvo.Entity<TicketWithScore>(t => t.OnCreated("scored", (AlvoCreated<TicketWithScore> _) => Task.CompletedTask)));

        result.Problems.ShouldHaveSingleItem().Message.ShouldBe("TicketWithScore.Score is a Double, which no field type holds.");
        result.Bindings.Handler("scored").ShouldBeNull();
        result.Bindings.Unbound.ShouldHaveSingleItem().Id.ShouldBe("scored");
    }

    [Fact]
    public void A_typed_application_event_payload_with_problems_refuses_boot_without_a_catalog() =>
        ExtensionBindingCompiler.Check(
            Registry(alvo => alvo.AddEventHandler<ScoredEvent, ScoredEventHandler>("scored-event")),
            CelFixtures.Compiler,
            EveryService()).Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/scored-event/record");

    [Fact]
    public void Every_typed_problem_is_reported_at_once()
    {
        var result = Compile(alvo => alvo
            .Entity<TicketWithVat>(t => t.OnCreated("missing", (AlvoCreated<TicketWithVat> _) => Task.CompletedTask))
            .Entity<TicketWithNumericCode>(t => t.OnCreated("mistyped", (AlvoCreated<TicketWithNumericCode> _) => Task.CompletedTask))
            .Entity<TicketWithSecret>(t => t.OnCreated("excluded", (AlvoCreated<TicketWithSecret> _) => Task.CompletedTask)));

        result.Problems.Select(problem => problem.Id).Distinct().Order().ShouldBe(["excluded", "missing", "mistyped"]);
        result.Bindings.Handlers.ShouldBeEmpty();
    }

    private static ExtensionProblem Problem<TRecord>()
        where TRecord : class
    {
        var problem = Compile(alvo => alvo.Entity<TRecord>(t => t.OnCreated("typed", (AlvoCreated<TRecord> _) => Task.CompletedTask)))
            .Problems.ShouldHaveSingleItem();
        problem.Locator.ShouldBe("extensions/typed/record");
        problem.Kind.ShouldBe("eventHandler");

        return problem;
    }

    [AlvoEvent("desk.scored")]
    private sealed record ScoredEvent(double Score);

    private sealed class ScoredEventHandler : IAlvoEventHandler<ScoredEvent>
    {
        public Task HandleAsync(ScoredEvent message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
