using MMLib.Alvo.Data;

using NSubstitute;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary>Typed reads (spec 2026-10-14 §5): the entity the type names, the translated filter, the caller's rules, typed rows.</summary>
public sealed class TypedReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_get_reads_the_entity_the_type_names_and_maps_the_record()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        session.GetAsync("orders", id, Ct).Returns(Task.FromResult<AlvoRecord?>(OrderRecord()));

        var order = await session.GetAsync<Order>(id, Ct);

        order.ShouldNotBeNull().Status.ShouldBe(OrderStatus.Approved);
    }

    [Fact]
    public async Task A_typed_get_of_a_record_the_caller_cannot_see_is_null()
    {
        var session = Substitute.For<IAlvoSession>();
        session.GetAsync("orders", Arg.Any<Guid>(), Ct).Returns(Task.FromResult<AlvoRecord?>(null));

        (await session.GetAsync<Order>(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_typed_query_sends_the_translated_filter_and_the_limit_and_maps_the_rows()
    {
        var session = Session(out var sent);

        var orders = await session.QueryAsync<Order>(x => x.Status == OrderStatus.Approved && x.Total > 10m, limit: 25, Ct);

        orders.ShouldHaveSingleItem().Note.ShouldBe("fragile");
        var query = sent.ShouldHaveSingleItem();
        (query.Entity, query.Limit).ShouldBe(("orders", 25));
        FilterText.Of(query.Filter!).ShouldBe("and(status eq 'ok', total gt 10)");
    }

    [Fact]
    public async Task A_captured_value_is_read_again_on_every_call()
    {
        var session = Session(out var sent);
        var note = "first";

        await session.QueryAsync<Order>(x => x.Note == note, limit: 5, Ct);
        note = "second";
        await session.QueryAsync<Order>(x => x.Note == note, limit: 5, Ct);

        sent.Select(query => FilterText.Of(query.Filter!)).ShouldBe(["note eq 'first'", "note eq 'second'"]);
    }

    [Fact]
    public async Task A_typed_query_needs_a_positive_limit() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Session(out _).QueryAsync<Order>(x => x.IsPaid, limit: 0, Ct));

    [Fact]
    public async Task A_type_without_an_entity_attribute_cannot_be_read() =>
        (await Should.ThrowAsync<InvalidOperationException>(() => Substitute.For<IAlvoSession>().GetAsync<Unmarked>(Guid.NewGuid(), Ct)))
            .Message.ShouldContain("[AlvoEntity(");

    [Fact]
    public async Task A_query_typed_over_a_bare_record_is_refused_since_it_names_no_entity() =>
        (await Should.ThrowAsync<InvalidOperationException>(() => Session(out _).QueryAsync<AlvoRecord>(x => (string?)x["note"] == "a", limit: 5, Ct)))
            .Message.ShouldContain("[AlvoEntity(");

    [Fact]
    public async Task A_type_mapped_to_every_entity_cannot_be_read()
    {
        var session = Substitute.For<IAlvoSession>();

        (await Should.ThrowAsync<InvalidOperationException>(() => session.GetAsync<EveryEntity>(Guid.NewGuid(), Ct)))
            .Message.ShouldContain("EveryEntity");
        await Should.ThrowAsync<InvalidOperationException>(() => session.QueryAsync<EveryEntity>(x => x.Title == "a", limit: 5, Ct));
        await session.DidNotReceiveWithAnyArgs().GetAsync(default!, default, Ct);
        await session.DidNotReceiveWithAnyArgs().QueryAsync(default!, Ct);
    }

    [Fact]
    public async Task An_untranslatable_query_is_refused_at_its_call_naming_the_entity() =>
        (await Should.ThrowAsync<AlvoTranslationException>(() => Session(out _).QueryAsync<Order>(x => x.Note.Trim() == "a", limit: 5, Ct)))
            .Message.ShouldContain("in a typed query on 'orders'");

    [Fact]
    public async Task The_data_port_s_typed_query_reads_as_the_caller_it_is_given()
    {
        var data = Substitute.For<IAlvoData>();
        var caller = new AlvoContext { User = new UserId(Guid.NewGuid()), Roles = new HashSet<Role> { Role.Authenticated } };
        data.QueryAsync(Arg.Any<AlvoQuery>(), caller, Ct).Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));

        var orders = await data.QueryAsync<Order>(x => x.IsPaid, limit: 10, caller, Ct);

        orders.ShouldHaveSingleItem();
        await data.Received(1).QueryAsync(Arg.Is<AlvoQuery>(query => query.Entity == "orders" && query.Limit == 10), caller, Ct);
    }

    private static IAlvoSession Session(out List<AlvoQuery> sent)
    {
        var queries = new List<AlvoQuery>();
        var session = Substitute.For<IAlvoSession>();
        session.QueryAsync(Arg.Do<AlvoQuery>(queries.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));
        sent = queries;

        return session;
    }
}
