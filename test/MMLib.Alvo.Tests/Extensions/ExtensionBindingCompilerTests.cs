using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// Boot validation of handler registrations (spec 2026-10-14 §4.2): every problem is found, each with id, kind, locator
/// and fix, and a handler with a problem is never bound.
/// </summary>
public sealed class ExtensionBindingCompilerTests
{
    [Fact]
    public void A_handler_on_a_known_entity_binds_with_its_compiled_condition()
    {
        var result = Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("ticket-resolved", OnUpdated, when: "changed(status) && new.status == 'resolved'")));

        result.Problems.ShouldBeEmpty();
        var bound = result.Bindings.Handler("ticket-resolved").ShouldNotBeNull();
        bound.Conditions["tickets"].Expression.ShouldNotBeNull();
    }

    [Fact]
    public void A_when_typo_is_refused_at_its_locator()
    {
        var problems = Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("ticket-resolved", OnUpdated, when: "new.stauts == 'resolved'")))
            .Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem =>
            problem.Id == "ticket-resolved" && problem.Kind == "eventHandler" && problem.Locator == "extensions/ticket-resolved/when");
    }

    [Fact]
    public void Every_problem_is_reported_at_once_and_none_of_their_handlers_binds()
    {
        var result = Compile(
            alvo => alvo
                .Entity("tickets", t => t.OnUpdated("typo", OnUpdated, when: "new.stauts == 'x'"))
                .Entity("tikets", t => t.OnCreated("misspelled", OnCreated))
                .Entity("notes", n => n.OnCreated("needs-a-clock", (AlvoCreated<AlvoRecord> _, TimeProvider clock) => Task.CompletedTask))
                .Entity("notes", n => n.OnCreated("fine", OnCreated)),
            NothingButTheBasics());

        result.Problems.Select(problem => problem.Id).Distinct().ShouldBe(["typo", "misspelled", "needs-a-clock"], ignoreOrder: true);
        result.Problems.Single(problem => problem.Id == "misspelled").Fix.ShouldContain("Did you mean 'tickets'?");
        result.Bindings.Handlers.Select(handler => handler.Id).ShouldBe(["fine"]);
    }

    [Theory]
    [InlineData("created", "old.status == 'open'")]
    [InlineData("deleted", "new.status == 'open'")]
    [InlineData("saved", "old.status == 'open'")]
    [InlineData("saved", "changed(status)")]
    public void A_condition_reading_an_image_the_verb_s_event_lacks_is_refused(string verb, string when) =>
        Compile(alvo => alvo.Entity("tickets", t => Register(t, verb, when))).Problems.ShouldHaveSingleItem().Locator
            .ShouldBe("extensions/on-it/when");

    [Theory]
    [InlineData("new.secret == 'x'")]
    [InlineData("changed(secret)")]
    public void A_condition_reading_a_field_left_out_of_the_event_images_is_refused(string when) =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("on-it", OnUpdated, when: when))).Problems.ShouldHaveSingleItem()
            .Message.ShouldContain("events.exclude");

    [Fact]
    public void A_star_registration_whose_condition_reads_a_field_is_refused()
    {
        var problems = Compile(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved, when: "new.title == 'x'"))).Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem => problem.Locator == "extensions/audit-all/when" && problem.Fix.Contains("@user and @tenant"));
    }

    [Fact]
    public void A_star_registration_may_test_the_caller() =>
        Compile(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved, when: "'admin' in @user.roles"))).Problems.ShouldBeEmpty();

    [Fact]
    public void An_application_event_handler_s_condition_cannot_read_the_payload()
    {
        var problems = Compile(alvo => alvo.AddEventHandler("on-approved", "orders.approved", OnRecord, when: "new.total > 1")).Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem => problem.Fix.Contains("inside the handler"));
    }

    [Fact]
    public void The_star_among_entity_names_is_refused() =>
        Compile(alvo => alvo.Entities(["tickets", "*"], e => e.OnCreated("mixed", OnCreated))).Problems.ShouldHaveSingleItem()
            .Locator.ShouldBe("extensions/mixed/entity");

    [Theory]
    [InlineData("Send_Confirmation")]
    [InlineData("a_b")]
    public void An_id_outside_the_extension_grammar_is_refused(string id) =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated(id, OnCreated))).Problems.ShouldHaveSingleItem()
            .Locator.ShouldBe($"extensions/{id}/id");

    [Fact]
    public void An_id_registered_twice_is_refused_even_as_a_retirement() =>
        Compile(alvo => alvo
                .Entity("tickets", t => t.OnCreated("sync", OnCreated))
                .AddRetiredExtension("sync", AlvoRetirement.Discard))
            .Problems.ShouldHaveSingleItem().Message.ShouldContain("registered 2 times");

    [Fact]
    public void Two_ids_deriving_one_event_name_segment_are_refused()
    {
        var problem = Compile(alvo => alvo
                .Entity("tickets", t => t.OnCreated("a-b", OnCreated))
                .AddRetiredExtension("a_b", AlvoRetirement.Discard))
            .Problems.ShouldHaveSingleItem();

        problem.Message.ShouldContain("'a-b' and 'a_b'");
    }

    [Fact]
    public void A_timeout_that_is_not_positive_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("instant", OnCreated, configure: o => o.Timeout = TimeSpan.Zero)))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/instant/timeout");

    [Fact]
    public void A_retirement_registered_before_a_colliding_handler_still_unbinds_the_handler()
    {
        var result = Compile(alvo => alvo
            .AddRetiredExtension("a_b", AlvoRetirement.Discard)
            .Entity("tickets", t => t.OnCreated("a-b", OnCreated)));

        var problem = result.Problems.ShouldHaveSingleItem();
        problem.Id.ShouldBe("a-b");
        problem.Kind.ShouldBe(ExtensionBindingCompiler.HandlerKind);
        result.Bindings.Handlers.ShouldBeEmpty();
        result.Bindings.Unbound.Select(handler => handler.Id).ShouldBe(["a-b"]);
    }

    [Fact]
    public void A_timeout_longer_than_the_cap_is_refused()
    {
        var problem = Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("forever", OnCreated, configure: o => o.Timeout = TimeSpan.MaxValue)))
            .Problems.ShouldHaveSingleItem();

        problem.Locator.ShouldBe("extensions/forever/timeout");
        problem.Fix.ShouldContain("1 hour");
    }

    [Fact]
    public void A_timeout_of_exactly_the_cap_binds() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("hour", OnCreated, configure: o => o.Timeout = TimeSpan.FromHours(1))))
            .Problems.ShouldBeEmpty();

    [Fact]
    public void A_run_as_outside_the_enum_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("who", OnCreated, configure: o => o.RunAs = (AlvoRunAs)7)))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/who/runAs");

    [Fact]
    public void An_ordering_outside_the_enum_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("when", OnCreated, configure: o => o.Ordering = (AlvoOrdering)(-1))))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/when/ordering");

    [Fact]
    public void An_application_event_handler_may_test_the_caller() =>
        Compile(alvo => alvo.AddEventHandler("on-approved", "orders.approved", OnRecord, when: "'admin' in @user.roles"))
            .Problems.ShouldBeEmpty();

    [Fact]
    public void A_star_condition_calling_changed_is_refused() =>
        Compile(alvo => alvo.Entity("*", all => all.OnUpdated("audit-all", OnUpdated, when: "changed(title)")))
            .Problems.ShouldNotBeEmpty();

    [Fact]
    public void A_deleted_condition_reading_an_excluded_field_through_the_pre_image_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnDeleted("on-it", OnDeleted, when: "old.secret == 'x'")))
            .Problems.ShouldHaveSingleItem().Message.ShouldContain("events.exclude");

    [Fact]
    public void A_handler_on_two_entities_carries_a_condition_compiled_against_each()
    {
        var bound = Compile(alvo => alvo.Entities(["tickets", "notes"], e => e.OnCreated("both", OnCreated, when: "has(new.id)")))
            .Bindings.Handler("both").ShouldNotBeNull();

        bound.Conditions.Keys.ShouldBe(["tickets", "notes"], ignoreOrder: true);
    }

    [Fact]
    public void Two_handlers_stating_one_order_on_one_operation_tie()
    {
        var tie = Compile(alvo => alvo.Entity("tickets", t => t
                .OnCreated("first", OnCreated, configure: o => o.Order = 5)
                .OnSaved("second", OnSaved, configure: o => o.Order = 5)))
            .Ties.ShouldHaveSingleItem();

        tie.Target.ShouldBe("tickets created");
        tie.Ids.ShouldBe(["first", "second"]);
    }

    [Fact]
    public void A_stated_order_equal_to_another_handler_s_default_ties() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated).OnCreated("second", OnCreated, configure: o => o.Order = 0)))
            .Ties.ShouldHaveSingleItem().Ids.ShouldBe(["first", "second"]);

    [Fact]
    public void Handlers_that_never_stated_an_order_do_not_tie() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated).OnCreated("second", OnCreated))).Ties.ShouldBeEmpty();

    private static AlvoEntityBuilder Register(AlvoEntityBuilder tickets, string verb, string when) => verb switch
    {
        "created" => tickets.OnCreated("on-it", OnCreated, when: when),
        "deleted" => tickets.OnDeleted("on-it", OnDeleted, when: when),
        _ => tickets.OnSaved("on-it", OnSaved, when: when),
    };

    private static IServiceProviderIsService NothingButTheBasics()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(TimeProvider));

        return services;
    }
}
