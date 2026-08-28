using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Globalization;

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
            "orders.approved:orders/42",
            customMessage:
                "the type is in the partition key so a host publishing subject 'deals:<guid>' cannot order "
                + "itself into a real entity's partition when F7's partitioned claim (#150) reads the column");
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
