using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

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
    [InlineData("tenant_id")]
    [InlineData("created_at")]
    [InlineData("record id")]
    [InlineData("record created_at")]
    [InlineData("nested")]
    [InlineData("captured")]
    public void A_patch_naming_no_writable_field_is_refused(string which)
    {
        var refusal = Should.Throw<ArgumentException>(() => RefusedPatches[which]());

        refusal.ParamName.ShouldBe("field");
    }

    [Fact]
    public void A_patch_s_values_cannot_be_changed_past_its_checks()
    {
        var patch = AlvoPatch.For<Order>().Set(x => x.Note, "n");

        patch.Values.ShouldNotBeOfType<Dictionary<string, object?>>();
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, object?>)patch.Values).Add("id", Guid.Empty));
    }

    [Fact]
    public void A_wrapper_patch_writes_an_enum_and_a_whole_number_as_their_fields_hold_them()
    {
        var patch = AlvoPatch.For<OrderView>().Set(x => x.Record["status"], OrderStatus.Approved).Set(x => x.Record["rank"], 2);

        patch.Values.ShouldBe(new Dictionary<string, object?> { ["status"] = "ok", ["rank"] = 2L });
    }

    [Fact]
    public async Task A_patch_from_elsewhere_naming_a_managed_field_is_refused_before_anything_is_sent()
    {
        var session = Substitute.For<IAlvoSession>();
        var foreign = Substitute.For<IAlvoPatch<Order>>();
        foreign.Values.Returns(new Dictionary<string, object?> { ["note"] = "n", ["created_by"] = "someone" });

        var refusal = await Should.ThrowAsync<ArgumentException>(() => session.UpdateAsync(Guid.NewGuid(), foreign, cancellationToken: Ct));

        refusal.ParamName.ShouldBe("patch");
        refusal.Message.ShouldContain("created_by");
        await session.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default!, default, Ct);
    }

    [Fact]
    public async Task A_typed_update_of_a_type_alvo_cannot_map_is_refused_before_anything_is_sent()
    {
        var session = Substitute.For<IAlvoSession>();

        await Should.ThrowAsync<InvalidOperationException>(() => session.UpdateAsync(Guid.NewGuid(), AlvoPatch.For<UncoveredOrder>().Set(x => x.Note, "n"), cancellationToken: Ct));
        await session.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default!, default, Ct);
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
    public async Task Exists_asks_for_one_row_s_id_through_the_translated_filter()
    {
        var session = Substitute.For<IAlvoSession>();
        AlvoQuery? sent = null;
        session.QueryAsync(Arg.Do<AlvoQuery>(query => sent = query), Ct)
            .Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));

        (await session.ExistsAsync<Order>(x => x.Reference == "R-1", Ct)).ShouldBeTrue();

        (sent!.Entity, sent.Limit).ShouldBe(("orders", 1));
        sent.Select.ShouldBe(["id"]);
        FilterText.Of(sent.Filter!).ShouldBe("ref_no eq 'R-1'");
    }

    [Fact]
    public async Task A_typed_create_sends_the_tenant_it_maps_but_never_an_audit_column()
    {
        var tenant = Guid.NewGuid();
        var (session, sent) = Capturing();

        await session.CreateAsync(Managed(tenant), Ct);

        sent.ShouldHaveSingleItem().Keys.ShouldBe(["tenant_id", "note"], ignoreOrder: true);
        sent[0]["tenant_id"].ShouldBe(tenant);
    }

    [Fact]
    public async Task A_typed_replace_sends_neither_the_tenant_nor_an_audit_column()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.ReplaceAsync("orders", id, Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), null, Ct)
            .Returns(Task.FromResult(AlvoReplaceResult.ReplacedRow(OrderRecord())));

        await session.ReplaceAsync(id, Managed(Guid.NewGuid()), cancellationToken: Ct);

        sent.ShouldNotBeNull().Keys.ShouldBe(["note"]);
    }

    [Fact]
    public async Task A_typed_create_with_no_tenant_mapped_as_null_sends_none()
    {
        var (session, sent) = Capturing();

        await session.CreateAsync(new OrderWithManaged { Note = "n" }, Ct);

        sent.ShouldHaveSingleItem().Keys.ShouldBe(["note"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_typed_create_whose_tenant_is_the_empty_uuid_is_refused_before_anything_is_sent_whoever_the_caller_is(bool asSystem)
    {
        var session = Substitute.For<IAlvoSession>();
        session.Context.Returns(asSystem ? AlvoContext.System(tenant: null) : AlvoContext.System(new TenantId(Guid.NewGuid())));

        var refusal = await Should.ThrowAsync<ArgumentException>(() => session.CreateAsync(new OrderWithTenant { Note = "unset tenant" }, Ct));

        refusal.ParamName.ShouldBe("record");
        refusal.Message.ShouldContain("Guid?");
        await session.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_wrapper_whose_record_carries_the_empty_uuid_as_its_tenant_is_refused()
    {
        var session = Substitute.For<IAlvoSession>();
        var record = new AlvoRecord(new Dictionary<string, object?> { ["note"] = "n", ["tenant_id"] = Guid.Empty.ToString() });

        await Should.ThrowAsync<ArgumentException>(() => session.CreateAsync(new OrderView(record), Ct));
        await session.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_typed_create_with_a_tenant_set_sends_it()
    {
        var tenant = Guid.NewGuid();
        var (session, sent) = Capturing();

        await session.CreateAsync(new OrderWithTenant { TenantId = tenant, Note = "n" }, Ct);

        sent.ShouldHaveSingleItem()["tenant_id"].ShouldBe(tenant);
    }

    [Fact]
    public async Task A_typed_create_never_sends_a_computed_or_rollup_field()
    {
        var (inner, sent) = Capturing(TotalsRecord());

        var order = await new SchemaSession(inner, OrdersWithTotalsSchema).CreateAsync(new OrderWithTotals { Note = "n", LineTotal = 5m, Label = "L" }, Ct);

        order.ShouldNotBeNull().LineTotal.ShouldBe(9m);
        sent.ShouldHaveSingleItem().Keys.ShouldBe(["note"]);
    }

    [Fact]
    public async Task A_typed_replace_never_sends_a_computed_or_rollup_field()
    {
        var inner = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        IReadOnlyDictionary<string, object?>? sent = null;
        inner.ReplaceAsync("orders", id, Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), null, Ct)
            .Returns(Task.FromResult(AlvoReplaceResult.ReplacedRow(TotalsRecord())));

        (await new SchemaSession(inner, OrdersWithTotalsSchema).ReplaceAsync(id, new OrderWithTotals { Note = "n" }, cancellationToken: Ct)).ShouldNotBeNull();

        sent.ShouldNotBeNull().Keys.ShouldBe(["note"]);
    }

    [Fact]
    public async Task A_wrapper_read_back_with_its_computed_and_rollup_fields_creates_without_them()
    {
        var (inner, sent) = Capturing(TotalsRecord());

        await new SchemaSession(inner, OrdersWithTotalsSchema).CreateAsync(new OrderView(TotalsRecord()), Ct);

        sent.ShouldHaveSingleItem().Keys.ShouldNotContain("line_total");
        sent[0].Keys.ShouldNotContain("label");
        sent[0]["note"].ShouldBe("n");
    }

    [Fact]
    public async Task A_session_that_cannot_name_the_schema_sends_every_mapped_field_for_the_port_to_judge()
    {
        var (session, sent) = Capturing(TotalsRecord());

        await session.CreateAsync(new OrderWithTotals { Note = "n", LineTotal = 5m, Label = "L" }, Ct);

        sent.ShouldHaveSingleItem().Keys.ShouldBe(["note", "line_total", "label"], ignoreOrder: true);
    }

    private static Dictionary<string, Func<object>> RefusedPatches => new(StringComparer.Ordinal)
    {
        ["summary"] = () => AlvoPatch.For<Order>().Set(x => x.Summary, "s"),
        ["id"] = () => AlvoPatch.For<Order>().Set(x => x.Id, Guid.Empty),
        ["tenant_id"] = () => AlvoPatch.For<OrderWithManaged>().Set(x => x.TenantId, Guid.NewGuid()),
        ["created_at"] = () => AlvoPatch.For<OrderWithManaged>().Set(x => x.CreatedAt, DateTimeOffset.UnixEpoch),
        ["record id"] = () => AlvoPatch.For<OrderView>().Set(x => x.Record["id"], Guid.NewGuid()),
        ["record created_at"] = () => AlvoPatch.For<OrderView>().Set(x => x.Record["created_at"], "2026-01-01T00:00:00Z"),
        ["nested"] = () => AlvoPatch.For<Order>().Set(x => x.Note.Length, 3),
        ["captured"] = Captured,
    };

    private static object Captured()
    {
        var other = new Order { Note = "elsewhere" };

        return AlvoPatch.For<Order>().Set(x => other.Note, "n");
    }

    private static OrderWithManaged Managed(Guid tenant) => new()
    {
        TenantId = tenant,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        DeletedAt = DateTimeOffset.UnixEpoch,
        Note = "n",
    };

    private static AlvoRecord TotalsRecord() =>
        new(new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["note"] = "n", ["line_total"] = 9m, ["label"] = "n!" });

    /// <summary>A session that records every create's values and answers <paramref name="stored"/>, else <see cref="TypedFixtures.OrderRecord"/>.</summary>
    private static (IAlvoSession Session, List<IReadOnlyDictionary<string, object?>> Sent) Capturing(AlvoRecord? stored = null)
    {
        var session = Substitute.For<IAlvoSession>();
        var sent = new List<IReadOnlyDictionary<string, object?>>();
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(sent.Add), Ct)
            .Returns(Task.FromResult(stored ?? OrderRecord()));

        return (session, sent);
    }

    /// <summary>A session that names the applied schema, as Alvo's own do; every call goes to <paramref name="inner"/>.</summary>
    private sealed class SchemaSession(IAlvoSession inner, EntitySchema entity) : IAlvoSession, IAlvoSessionSchema
    {
        public AlvoContext Context => inner.Context;

        public EntitySchema? EntityOf(string entityName) => string.Equals(entityName, entity.Name, StringComparison.Ordinal) ? entity : null;

        public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) => inner.QueryAsync(query, cancellationToken);

        public Task<AlvoRecord?> GetAsync(string entityName, Guid id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(entityName, id, cancellationToken);

        public Task<AlvoRecord> CreateAsync(string entityName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(entityName, values, cancellationToken);

        public Task<AlvoRecord> UpdateAsync(
            string entityName, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(entityName, id, values, precondition, cancellationToken);

        public Task<AlvoReplaceResult> ReplaceAsync(
            string entityName, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(entityName, id, values, precondition, cancellationToken);

        public Task DeleteAsync(string entityName, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(entityName, id, precondition, cancellationToken);
    }
}
