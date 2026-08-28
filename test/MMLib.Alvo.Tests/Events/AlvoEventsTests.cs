using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// A host's own <c>Publish</c>, and the namespace guard that is the reason it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard is the security fact and everything else here supports it.</b> Without it a host could mint
/// <c>entity.orders.updated</c>, and every after-hook and descriptor rule subscribing to that name would fire
/// on an event with a partition key and provenance for a record nobody wrote — indistinguishable, at the
/// point of delivery, from a real data change.
/// </para>
/// <para>
/// <b>The other half of the guarantee lives in <c>EventSubscriptionsTests</c></b>, where the catalog is:
/// <c>An_event_a_host_published_selects_no_after_hook</c> drives a real published envelope through the real
/// matcher, because "indistinguishable from a data change" is a claim about that reader and about nothing
/// else. Asserting a missing prefix here would be a claim about strings.
/// </para>
/// </remarks>
public sealed class AlvoEventsTests
{
    [Theory]
    [InlineData("entity.orders.updated")]
    [InlineData("entity.deals.created")]
    [InlineData("auth.user.login")]
    [InlineData("storage.file.uploaded")]
    public async Task Publish_refuses_a_reserved_namespace(string type)
    {
        var store = new RecordingOutboxStore();

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => Publisher(store).PublishAsync(type, "orders/42", null, Caller));

        refusal.ParamName.ShouldBe("type");
        refusal.Message.ShouldContain("reserved");
        store.Appended.ShouldBeEmpty("a refused name must leave no entry a subscriber could ever claim");
    }

    /// <summary>
    /// A name outside the three namespaces is published, so the guard refuses the forgery and not the feature.
    /// </summary>
    [Fact]
    public async Task Publish_appends_one_entry_carrying_the_guarded_name()
    {
        var store = new RecordingOutboxStore();

        await Publisher(store).PublishAsync(
            "orders.approved",
            "orders/42",
            new Dictionary<string, object?> { ["total"] = 99 },
            Caller,
            TestContext.Current.CancellationToken);

        var published = store.Appended.ShouldHaveSingleItem();
        published.Type.ShouldBe("orders.approved");
        published.Subject.ShouldBe("orders/42");
        published.PartitionKey.ShouldBe(
            "custom.event:orders/42",
            customMessage:
                "a fixed dotted marker keeps the key out of any data entity's partition when F7's partitioned "
                + "claim (#150) reads the column, without splitting one subject across partitions");
        published.AuthId.ShouldBe(Caller.User.Value.ToString());
        published.Data.Record!["total"].ShouldBe(99);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("Orders.Approved")]
    [InlineData("orders..approved")]
    [InlineData("orders.approved.")]
    [InlineData("orders approved")]
    [InlineData("9orders.approved")]

    // A trailing control character is its own case: .NET's `$` matches before a trailing \n, so `^...$`
    // admitted "orders.approved\n" and would have put a newline into event_type and partition_key —
    // two event types that print identically, and a forged line in any log that names one. The guard
    // anchors \A...\z for this reason, and these three are what hold it.
    [InlineData("orders.approved\n")]
    [InlineData("orders.approved\r\n")]
    [InlineData("orders.approved\u0000")]
    public async Task Publish_refuses_a_malformed_name(string type)
    {
        var store = new RecordingOutboxStore();

        await Should.ThrowAsync<ArgumentException>(
            () => Publisher(store).PublishAsync(type, "orders/42", null, Caller));

        store.Appended.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Publish_refuses_a_blank_name(string? type)
        => await Should.ThrowAsync<ArgumentException>(
            () => Publisher(new RecordingOutboxStore()).PublishAsync(type!, "orders/42", null, Caller));

    /// <summary>
    /// <b>The guard is on the type the port accepts, so resolving <see cref="IOutboxStore"/> directly does not
    /// get around it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact the first draft of this PR did not have, and two independent reviews found the hole it
    /// left: <see cref="IOutboxStore"/> is public and DI-registered and <see cref="AlvoEvent"/> is a public
    /// record with public initializers, so when <c>AppendAsync</c> took a bare envelope a host could append
    /// <c>entity.orders.updated</c> with <c>authtype: system</c> and a payload of its choosing — firing every
    /// after-hook subscribed to the real name, one layer below the guard.
    /// </para>
    /// <para>
    /// It asserts the refusal at <see cref="AlvoCustomEvent.Create"/> rather than through the publisher,
    /// because that is the claim: the queue has exactly one door and the door is guarded. A fact that only
    /// went through <c>PublishAsync</c> would stay green if the check were moved back out of the type.
    /// </para>
    /// </remarks>
    /// <param name="type">A reserved name a host might try to forge.</param>
    [Theory]
    [InlineData("entity.orders.updated")]
    [InlineData("auth.user.login")]
    [InlineData("storage.file.uploaded")]
    public void The_only_door_into_the_queue_refuses_a_reserved_name(string type)
        => Should.Throw<ArgumentException>(() => AlvoCustomEvent.Create(Forged(type)))
            .Message.ShouldContain("reserved");

    /// <summary>
    /// <b>The reserved set cannot be emptied through the interface it is handed out as.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was an <c>IReadOnlySet&lt;string&gt;</c> over a live <c>HashSet</c>, so a host could downcast it once
    /// at startup, call <c>Clear()</c>, and disable the guard process-wide — after which
    /// <see cref="AlvoCustomEvent.Create"/> accepts <c>entity.orders.updated</c> again. A read-only
    /// <em>interface</em> over a mutable set is not a read-only set, and the whole structural guarantee rests
    /// on this one collection.
    /// </para>
    /// <para>
    /// <b>The fact asserts that mutating throws, not that the cast fails</b>, because
    /// <see cref="System.Collections.Frozen.FrozenSet{T}"/> <em>does</em> implement
    /// <c>ICollection&lt;string&gt;</c> — its mutators throw instead of being absent. A first draft asserted
    /// the downcast returned <see langword="null"/> and went red for the right reason. The guarantee is then
    /// stated end to end: the set still reserves the name, and the forgery is still refused.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_reserved_namespaces_cannot_be_emptied_by_a_host()
    {
        var downcast = (ICollection<string>)AlvoEventName.ReservedNamespaces;

        Should.Throw<NotSupportedException>(() => downcast.Clear());
        Should.Throw<NotSupportedException>(() => downcast.Remove("entity"));
        AlvoEventName.IsReservedNamespace("entity").ShouldBeTrue();
        Should.Throw<ArgumentException>(
            () => AlvoCustomEvent.Create(Forged("entity.orders.updated")));
    }

    /// <summary>
    /// <b>A well-named custom event still cannot claim a data entity's partition.</b>
    /// </summary>
    /// <remarks>
    /// The name guard alone left this open: <c>crm.thing.happened</c> passes it, and nothing stopped the
    /// envelope carrying <c>PartitionKey = "deals:&lt;rowId&gt;"</c> — which orders the custom event inside a
    /// real entity's partition the day F7's partitioned claim (#150) reads the column. The same lesson as the
    /// first bypass: a guarantee is only as strong as the narrowest door that reaches it, and
    /// <see cref="IOutboxStore.AppendAsync"/> is a door.
    /// </remarks>
    [Theory]
    [InlineData("deals:3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("orders/42")]
    [InlineData("")]
    [InlineData("deals:")]
    public void A_custom_event_cannot_claim_another_partition(string partitionKey)
        => Should.Throw<ArgumentException>(
                () => AlvoCustomEvent.Create(WellNamed(partitionKey)))
            .Message.ShouldContain("data entity's partition");

    /// <summary>Any first segment carrying a dot is a host's own partition, and is accepted.</summary>
    /// <remarks>
    /// It need not be this event's own type — the first draft required that and refused host keys that were
    /// never a hazard, while contradicting the fixed marker <c>PublishAsync</c> builds. What makes a key safe
    /// is only that no entity can be named like its first segment.
    /// </remarks>
    /// <param name="partitionKey">A key whose first segment contains a dot.</param>
    [Theory]
    [InlineData("crm.thing.happened:orders/42")]
    [InlineData("custom.event:orders/42")]
    [InlineData("a.b:c")]
    public void A_custom_event_in_its_own_partition_is_accepted(string partitionKey)
        => AlvoCustomEvent.Create(WellNamed(partitionKey)).Envelope.PartitionKey.ShouldBe(partitionKey);

    /// <summary>
    /// <b>A payload the envelope cannot express is refused at the call, not from inside the queue.</b>
    /// </summary>
    /// <remarks>
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> accepts the obvious things to relay from inbound JSON
    /// — a nested dictionary, an array, a <c>JsonElement</c> — and the envelope writer accepts none of them.
    /// Before this, the refusal surfaced from inside <see cref="IOutboxStore.AppendAsync"/> as a
    /// <c>NotSupportedException</c> advising the caller to use "one of the field types the schema allows",
    /// which says nothing to someone publishing a custom event that has no schema.
    /// </remarks>
    /// <param name="unwritable">One payload value the wire format cannot carry.</param>
    [Theory]
    [MemberData(nameof(UnwritablePayloads))]
    public async Task Publish_refuses_a_payload_the_envelope_cannot_express(object unwritable)
    {
        var store = new RecordingOutboxStore();

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => Publisher(store).PublishAsync(
                "orders.approved",
                "orders/42",
                new Dictionary<string, object?> { ["value"] = unwritable },
                Caller,
                TestContext.Current.CancellationToken));

        refusal.ParamName.ShouldBe("data");
        store.Appended.ShouldBeEmpty("a refused payload must leave no entry behind");
    }

    public static TheoryData<object> UnwritablePayloads() =>
    [
        new Dictionary<string, object?> { ["nested"] = 1 },
        new[] { 1, 2, 3 },
        JsonDocument.Parse("{}").RootElement,
        new Uri("https://example.com"),
        TimeSpan.FromMinutes(1),
    ];

    /// <summary>
    /// <b>Every scalar the payload does accept survives the real serializer.</b>
    /// </summary>
    /// <remarks>
    /// The other half of the refusal above, and the round trip nothing exercised: the publish facts assert
    /// against an in-memory store, so a payload that the envelope writer would reject — or silently reshape —
    /// never reached it.
    /// </remarks>
    [Fact]
    public async Task A_scalar_payload_survives_the_envelope_round_trip()
    {
        var store = new RecordingOutboxStore();
        var payload = new Dictionary<string, object?>
        {
            ["text"] = "approved",
            ["flag"] = true,
            ["count"] = 42,
            ["amount"] = 99.50m,
            ["id"] = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
            ["at"] = new DateTimeOffset(2026, 8, 28, 9, 30, 0, TimeSpan.Zero),
            ["nothing"] = null,
        };

        await Publisher(store).PublishAsync(
            "orders.approved", "orders/42", payload, Caller, TestContext.Current.CancellationToken);

        var written = AlvoEventJson.Read(AlvoEventJson.Write(store.Appended.ShouldHaveSingleItem()));
        written.Data.Record!.Values.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(payload.Keys.Order(StringComparer.Ordinal));
        written.Data.Record["text"].ShouldBe("approved");
        written.Data.Record["flag"].ShouldBe(true);
        written.Data.Record["nothing"].ShouldBeNull();
    }

    /// <summary>
    /// <b>Two event types about one subject share a partition, because ordering is promised per subject.</b>
    /// </summary>
    /// <remarks>
    /// The first draft keyed the partition <c>{type}:{subject}</c>, which split <c>orders.approved</c> and
    /// <c>orders.shipped</c> for one <c>orders/42</c> into different partitions while
    /// <see cref="IAlvoEvents"/>' own contract promised ordering per subject. Caught in review, and this is
    /// the fact that keeps the two from drifting apart again.
    /// </remarks>
    [Fact]
    public async Task Two_types_about_one_subject_share_a_partition()
    {
        var store = new RecordingOutboxStore();
        var publisher = Publisher(store);

        await publisher.PublishAsync(
            "orders.approved", "orders/42", null, Caller, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(
            "orders.shipped", "orders/42", null, Caller, TestContext.Current.CancellationToken);

        store.Appended.Select(e => e.PartitionKey).Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem("both events are about orders/42, so both order within one partition");
    }

    /// <summary>A null partition key is the factory's refusal, never a <see cref="NullReferenceException"/>.</summary>
    /// <remarks>
    /// <see cref="AlvoEvent.PartitionKey"/> is <c>required</c>, but <c>required</c> is satisfied by assigning
    /// <c>null!</c> — so the check reached <c>StartsWith</c> on a null and threw the wrong exception type out
    /// of a factory that promises <see cref="ArgumentException"/>. Caught in review.
    /// </remarks>
    [Fact]
    public void A_null_partition_key_is_refused_rather_than_dereferenced()
        => Should.Throw<ArgumentException>(() => AlvoCustomEvent.Create(WellNamed(null!)));

    /// <summary>A guarded name with a caller-chosen partition key, for the partition facts above.</summary>
    /// <param name="partitionKey">The key under test.</param>
    private static AlvoEvent WellNamed(string partitionKey)
    {
        var envelope = Forged("crm.thing.happened");
        return envelope with { PartitionKey = partitionKey };
    }

    /// <summary>An envelope shaped exactly like a real data event, which is what makes the refusal matter.</summary>
    /// <param name="type">The name being forged.</param>
    private static AlvoEvent Forged(string type)
    {
        var now = DateTimeOffset.UtcNow;
        var id = AlvoEventId.Create(now);

        return new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = "orders/42",
            PartitionKey = "orders:42",
            AuthType = AlvoEventAuthType.System,
            CorrelationId = id.ToString(),
            Data = new AlvoEventData(),
        };
    }

    private static AlvoEvents Publisher(IOutboxStore store) => new(store, TimeProvider.System);

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff", CultureInfo.InvariantCulture),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>The queue, reduced to the one question every fact here asks: what reached it.</summary>
    private sealed class RecordingOutboxStore : IOutboxStore
    {
        private readonly List<AlvoEvent> _appended = [];

        internal IReadOnlyList<AlvoEvent> Appended => _appended;

        public Task AppendAsync(AlvoCustomEvent customEvent, CancellationToken cancellationToken = default)
        {
            _appended.Add(customEvent.Envelope);
            return Task.CompletedTask;
        }

        public Task EnsureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
            string claimant, int batchSize, int maxAttempts, TimeSpan lease, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>([]);

        public Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
