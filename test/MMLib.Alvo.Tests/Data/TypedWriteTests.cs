using MMLib.Alvo.Data;

using NSubstitute;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary>Typed writes (spec 2026-10-14 §5, HX-07): the entity the type names, its mapped fields as the fields hold them, and the stored record back as the type.</summary>
public sealed class TypedWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_create_writes_its_entity_s_mapped_fields_as_the_fields_hold_them()
    {
        var session = Substitute.For<IAlvoSession>();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), Ct)
            .Returns(Task.FromResult(OrderRecord()));

        var order = await session.CreateAsync(
            new Order { Id = Guid.NewGuid(), Note = "fragile", Status = OrderStatus.Approved, Rank = 2, Reference = "R-1", Total = 12.5m, Quantity = 3 }, Ct);

        order.ShouldNotBeNull().Note.ShouldBe("fragile");
        sent.ShouldNotBeNull();
        sent["status"].ShouldBe("ok");
        sent["rank"].ShouldBe(2L);
        sent["ref_no"].ShouldBe("R-1");
        sent["quantity"].ShouldBe(3L);
        sent["total"].ShouldBe(12.5m);
        sent.ShouldNotContainKey("id");
        sent.ShouldNotContainKey("summary");
        sent.ShouldNotContainKey("owner_id");
    }

    [Fact]
    public async Task A_typed_create_leaves_out_a_null_property_so_its_descriptor_default_applies()
    {
        var session = Substitute.For<IAlvoSession>();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), Ct)
            .Returns(Task.FromResult(OrderRecord()));

        await session.CreateAsync(new Order { Note = "n" }, Ct);

        sent.ShouldNotBeNull();
        sent.Keys.ShouldNotContain("code");
        sent.Keys.ShouldNotContain("due_at");
        sent.Keys.ShouldNotContain("rank");
        (sent["is_paid"], sent["quantity"]).ShouldBe(((object?)false, (object?)0L), "a non-nullable property's default is a value");
    }

    [Fact]
    public async Task A_typed_create_answers_null_when_the_caller_cannot_read_the_row_back()
    {
        var session = Substitute.For<IAlvoSession>();
        session.CreateAsync("orders", Arg.Any<IReadOnlyDictionary<string, object?>>(), Ct)
            .Returns(Task.FromResult(new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid() })));

        (await session.CreateAsync(new Order { Note = "hidden" }, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_wrapper_writes_its_record_without_the_fields_alvo_manages()
    {
        var session = Substitute.For<IAlvoSession>();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), Ct)
            .Returns(Task.FromResult(OrderRecord()));
        var record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["note"] = "n", ["legacy_code"] = "L-1", ["created_at"] = "2026-01-01T00:00:00Z" });

        (await session.CreateAsync(new OrderView(record), Ct)).ShouldNotBeNull();

        sent.ShouldNotBeNull().Keys.ShouldBe(["note", "legacy_code"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_patch_sends_only_the_fields_it_sets_by_their_field_names()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.UpdateAsync("orders", id, Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), null, Ct)
            .Returns(Task.FromResult(OrderRecord()));

        var order = await session.UpdateAsync(id, AlvoPatch.For<Order>().Set(x => x.Status, OrderStatus.Approved).Set(x => x.Reference, "R-2").Set(x => x.Rank, (int?)null), cancellationToken: Ct);

        order.ShouldNotBeNull();
        sent.ShouldNotBeNull().Count.ShouldBe(3);
        sent["status"].ShouldBe("ok");
        sent["ref_no"].ShouldBe("R-2");
        sent["rank"].ShouldBeNull();
    }

    [Fact]
    public void A_patch_of_a_wrapper_names_its_record_keys()
        => AlvoPatch.For<OrderView>().Set(x => x.Record["legacy_code"], "L-2").Values.ShouldBe(new Dictionary<string, object?> { ["legacy_code"] = "L-2" });

    [Theory]
    [InlineData("summary")]
    [InlineData("id")]
    public void A_patch_naming_no_writable_field_is_refused(string which)
    {
        var refusal = Should.Throw<ArgumentException>(() => _ = which == "id"
            ? AlvoPatch.For<Order>().Set(x => x.Id, Guid.Empty)
            : AlvoPatch.For<Order>().Set(x => x.Summary, "s"));

        refusal.ParamName.ShouldBe("field");
    }

    [Fact]
    public async Task An_empty_patch_is_refused_before_anything_is_sent()
    {
        var session = Substitute.For<IAlvoSession>();

        await Should.ThrowAsync<ArgumentException>(() => session.UpdateAsync(Guid.NewGuid(), AlvoPatch.For<Order>(), cancellationToken: Ct));
        await session.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default!, default, Ct);
    }

    [Fact]
    public async Task A_typed_replace_writes_the_whole_record_under_the_id()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        session.ReplaceAsync("orders", id, Arg.Any<IReadOnlyDictionary<string, object?>>(), null, Ct)
            .Returns(Task.FromResult(AlvoReplaceResult.ReplacedRow(OrderRecord())));

        (await session.ReplaceAsync(id, new Order { Note = "fragile" }, cancellationToken: Ct)).ShouldNotBeNull().Status.ShouldBe(OrderStatus.Approved);
        await session.Received(1).ReplaceAsync(
            "orders", id, Arg.Is<IReadOnlyDictionary<string, object?>>(values => !values.ContainsKey("id") && values.ContainsKey("owner_id") && values["owner_id"] == null), null, Ct);
    }

    [Fact]
    public async Task Exists_asks_for_one_row_through_the_translated_filter()
    {
        var session = Substitute.For<IAlvoSession>();
        AlvoQuery? sent = null;
        session.QueryAsync(Arg.Do<AlvoQuery>(query => sent = query), Ct)
            .Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));

        (await session.ExistsAsync<Order>(x => x.Reference == "R-1", Ct)).ShouldBeTrue();

        (sent!.Entity, sent.Limit).ShouldBe(("orders", 1));
        FilterText.Of(sent.Filter!).ShouldBe("ref_no eq 'R-1'");
    }
}
