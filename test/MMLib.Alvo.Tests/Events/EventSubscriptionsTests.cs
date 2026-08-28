using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Which hooks one event is subscribed to: the entity and operation its <c>type</c> names, and then the
/// conditions that hold for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The condition is part of the subscription, not the run's first step</b> (base design <c>:583-592</c>).
/// §3.3 records the consequence of getting this wrong as a documented Directus defect: thousands of log entries
/// for runs that abort immediately on their condition. Alvo has the advantage by construction — the CEL
/// <c>Condition</c> profile is compiled at apply time, so the predicate is available here.
/// </para>
/// <para>
/// Every catalog here is built through <see cref="PolicyCatalog.TryBuild"/> and every condition is a real
/// compiled CEL expression judged by the real interpreter, so a fact about "the condition was false" is a fact
/// about the engine that will judge it in production rather than about a substitute that answered false.
/// </para>
/// </remarks>
public sealed class EventSubscriptionsTests : IDisposable
{
    /// <inheritdoc/>
    public void Dispose() => _logger.Dispose();

    [Fact]
    public void An_event_selects_only_the_hooks_of_its_own_operation()
    {
        var matched = Matching(CatalogWithAHookOnEveryPoint, Event("entity.deals.updated"));

        matched.ShouldHaveSingleItem().Path.ShouldContain("afterUpdate");
    }

    [Theory]
    [InlineData("entity.deals.created", "afterCreate")]
    [InlineData("entity.deals.updated", "afterUpdate")]
    [InlineData("entity.deals.deleted", "afterDelete")]
    public void The_type_the_driver_wrote_selects_the_point_named_after_that_operation(string type, string point)
        => Matching(CatalogWithAHookOnEveryPoint, Event(type))
            .ShouldHaveSingleItem().Path.ShouldContain(point);

    [Fact]
    public void An_event_for_an_entity_with_no_hooks_selects_nothing()
        => Matching(CatalogWithAHookOnEveryPoint, Event("entity.vehicles.created")).ShouldBeEmpty();

    /// <summary>
    /// An event type this build cannot parse selects nothing, rather than everything.
    /// </summary>
    /// <remarks>
    /// The emitting driver spells the type and this suite's subject reads it back, in two assemblies that share
    /// no constant — so the interesting direction is what happens when they disagree. Selecting nothing keeps a
    /// queue entry from a build that spoke a different grammar from running every hook on the entity; selecting
    /// everything would deliver an event nobody subscribed to.
    /// </remarks>
    [Theory]
    [InlineData("entity.deals.upserted")]
    [InlineData("entity.deals")]
    [InlineData("deals.updated")]
    [InlineData("automation.deals.updated")]
    [InlineData("entity..updated")]
    public void An_event_type_this_build_cannot_read_selects_nothing_rather_than_everything(string type)
        => Matching(CatalogWithAHookOnEveryPoint, Event(type)).ShouldBeEmpty();

    [Fact]
    public void A_hook_whose_condition_is_false_is_not_selected()
        => Matching(CatalogConditionedOnWinning, Updated(stage: "lead", was: "lead")).ShouldBeEmpty();

    [Fact]
    public void A_hook_whose_condition_holds_is_selected()
        => Matching(CatalogConditionedOnWinning, Updated(stage: "won", was: "lead")).ShouldHaveSingleItem();

    /// <summary>
    /// The pre-image reaches the evaluator, so <c>changed(...)</c> is answered against the row's own previous
    /// values rather than against nothing.
    /// </summary>
    /// <remarks>
    /// Without it every field reads as moved and the transition criterion — "fires exactly once, at the
    /// transition" — would be satisfied by a hook that fires on every write to an already-won deal.
    /// </remarks>
    [Fact]
    public void A_transition_condition_is_judged_against_the_events_own_pre_image()
        => Matching(CatalogConditionedOnWinning, Updated(stage: "won", was: "won")).ShouldBeEmpty(
            "changed(stage) must be false when the stage did not move");

    [Fact]
    public void A_hook_with_no_condition_is_always_selected()
        => Matching(CatalogWithAHookOnEveryPoint, Event("entity.deals.updated")).ShouldHaveSingleItem();

    /// <summary>
    /// A condition that throws must not select the hook, and must not take the batch down either: a broken
    /// predicate is a fail-closed refusal, exactly as an unprimed catalog denies every operation.
    /// </summary>
    [Fact]
    public void A_condition_that_throws_selects_nothing_rather_than_everything()
        => EventSubscriptions
            .Matching(CatalogConditionedOnWinning, Updated("won", "lead"), new ThrowingEvaluator(), _logger)
            .ShouldBeEmpty();

    /// <summary>
    /// The refusal is diagnosable at Debug and nothing louder: one line per event and per hook is the noise the
    /// whole execution-log criterion exists to prevent.
    /// </summary>
    [Fact]
    public void A_condition_that_throws_is_recorded_at_debug_naming_the_hook_and_the_event()
    {
        var @event = Updated("won", "lead");

        EventSubscriptions.Matching(
            CatalogConditionedOnWinning, @event, new ThrowingEvaluator(), _logger).ShouldBeEmpty();

        var line = _logger.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Debug);
        line.Message.ShouldContain("/entities/deals/hooks/afterUpdate/0");
        line.Message.ShouldContain(@event.Id.ToString());
        line.Exception.ShouldNotBeNull();
    }

    /// <summary>
    /// A condition's <c>@user.id</c> is the <b>envelope's</b> actor, not the process draining the queue.
    /// </summary>
    /// <remarks>
    /// The most ordinary after-hook condition there is — "notify the owner unless the owner is who changed it" —
    /// and against a dispatcher-wide <c>AlvoContext.System</c> it could not work: the positive form never matched
    /// and the negated form always did, because the comparison was against the framework's own reserved id. Both
    /// directions are asserted from one envelope so neither can pass by accident.
    /// </remarks>
    [Theory]
    [InlineData(Actor, false)]
    [InlineData(Bystander, true)]
    public void A_conditions_user_id_is_the_envelopes_actor_and_not_the_dispatcher(string owner, bool selected)
    {
        var matched = Matching(CatalogConditionedOnAnotherActor, OwnedBy(owner, actedBy: Actor));

        matched.Count.ShouldBe(selected ? 1 : 0, $"owner_id '{owner}' against an actor of '{Actor}'");
    }

    /// <summary>
    /// An event that records no actor selects no hook that asks who acted — rather than comparing the row
    /// against the reserved all-zero id, which means "no identity" and never a caller who owns those rows.
    /// </summary>
    [Fact]
    public void A_hook_reading_user_id_is_not_selected_when_the_event_records_no_actor()
    {
        var @event = OwnedBy(Bystander, actedBy: null);

        Matching(CatalogConditionedOnAnotherActor, @event).ShouldBeEmpty();

        var line = _logger.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Debug);
        line.Message.ShouldContain("@user.id");
        line.Message.ShouldContain(@event.Id.ToString());
    }

    /// <summary>
    /// The gate is on the reference and not on the event: a hook whose condition never names <c>@user.id</c> is
    /// selected for an actorless event exactly as before.
    /// </summary>
    /// <remarks>
    /// The non-vacuity control for the fact above — without it, "not selected" would also hold if the gate
    /// refused every hook on an anonymous write.
    /// </remarks>
    [Fact]
    public void A_hook_that_never_reads_user_id_is_still_selected_for_an_actorless_event()
        => Matching(CatalogWithAHookOnEveryPoint, OwnedBy(Bystander, actedBy: null))
            .ShouldHaveSingleItem().Path.ShouldContain("afterUpdate");

    /// <summary>
    /// <b>An event a host published selects no after-hook, and that is what the publish-time namespace guard
    /// buys.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard refuses <c>entity.</c>, <c>auth.</c> and <c>storage.</c> so a host cannot mint an event
    /// indistinguishable from a real data change. This is the half that says what "indistinguishable" means:
    /// the arbiter is this matcher, and a real envelope built by the real publisher reaches it selecting
    /// nothing — even against a catalog with a hook on every point of the entity it names.
    /// </para>
    /// <para>
    /// The envelope comes from <c>AlvoEvents</c> rather than being hand-built, so the fact cannot pass because
    /// the test wrote a type the publisher would never produce.
    /// </para>
    /// <para>
    /// <b>The name is the nearest legal forgery, and that is load-bearing.</b> <c>crm.deals.updated</c> has
    /// three segments, names an entity this catalog really has hooks on, and ends in a suffix this reader
    /// really maps — everything a data event has except the reserved namespace. A shorter name such as
    /// <c>deals.approved</c> would be turned away by the segment-count check before the prefix was ever
    /// compared, so it would pin the wrong half: measured, by inverting the prefix comparison and watching the
    /// two-segment version stay green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_event_a_host_published_selects_no_after_hook()
    {
        var store = new CapturingOutboxStore();
        await new AlvoEvents(store, TimeProvider.System).PublishAsync(
            "crm.deals.updated", "deals/42", null, AlvoContext.Anonymous, TestContext.Current.CancellationToken);

        Matching(CatalogWithAHookOnEveryPoint, store.Published.ShouldHaveSingleItem()).ShouldBeEmpty();
    }

    /// <summary>The queue, reduced to the one thing the fact above needs from it.</summary>
    private sealed class CapturingOutboxStore : IOutboxStore
    {
        private readonly List<AlvoEvent> _published = [];

        internal IReadOnlyList<AlvoEvent> Published => _published;

        public Task AppendAsync(AlvoCustomEvent customEvent, CancellationToken cancellationToken = default)
        {
            _published.Add(customEvent.Envelope);
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

    private readonly CapturingLogger _logger = new();

    private const string Actor = "019000aa-0000-7000-8000-00000000a001";
    private const string Bystander = "019000aa-0000-7000-8000-00000000b002";

    private IReadOnlyList<CompiledAfterHook> Matching(PolicyCatalog catalog, AlvoEvent @event) =>
        EventSubscriptions.Matching(catalog, @event, CelFixtures.Evaluator, _logger);

    /// <summary>
    /// The schema every catalog below is compiled against.
    /// </summary>
    /// <remarks>
    /// Declared before the catalogs on purpose: a static field initializer runs in declaration order, so a
    /// catalog built above this line would compile against <see langword="null"/>.
    /// </remarks>
    private static SchemaModel Schema { get; } = new([Entity("deals"), Entity("vehicles")]);

    private static PolicyCatalog CatalogWithAHookOnEveryPoint { get; } = Catalog(new EntityHooks
    {
        AfterCreate = [Hook()],
        AfterUpdate = [Hook()],
        AfterDelete = [Hook()],
    });

    private static PolicyCatalog CatalogConditionedOnWinning { get; } = Catalog(new EntityHooks
    {
        AfterUpdate = [Hook("changed(stage) && new.stage == 'won'")],
    });

    private static PolicyCatalog CatalogConditionedOnAnotherActor { get; } = Catalog(new EntityHooks
    {
        AfterUpdate = [Hook("new.owner_id != @user.id")],
    });

    private static AfterHook Hook(string? condition = null) =>
        new() { Condition = condition, Action = new WebhookAction { Endpoint = "crm-sync" } };

    private static PolicyCatalog Catalog(EntityHooks hooks)
    {
        PolicyCatalog.TryBuild(Descriptor(hooks), Schema, CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue($"expected a clean build, got: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}");

        return catalog!;
    }

    private static AlvoDescriptor Descriptor(EntityHooks hooks) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "test",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["deals"] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Hooks = hooks,
            },
            ["vehicles"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                ["crm-sync"] = new() { Url = "https://example.test/hook", SecretRef = "crm-sync-secret" },
            },
        },
    };

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "stage", Type = FieldType.Enum, EnumValues = ["lead", "won", "lost"] },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid },
        ],
    };

    /// <summary>One update of a deal, saying who owns the row and which credential changed it.</summary>
    /// <param name="owner">The row's <c>owner_id</c>.</param>
    /// <param name="actedBy">The envelope's <c>authid</c>, or <see langword="null"/> for an anonymous write.</param>
    private static AlvoEvent OwnedBy(string owner, string? actedBy) => Event(
        "entity.deals.updated",
        record: Record(("owner_id", Guid.Parse(owner))),
        oldRecord: Record(("owner_id", Guid.Parse(owner))),
        authId: actedBy);

    private static AlvoEvent Updated(string stage, string was) => Event(
        "entity.deals.updated",
        record: Record(("stage", stage)),
        oldRecord: Record(("stage", was)),
        changed: string.Equals(stage, was, StringComparison.Ordinal) ? [] : ["stage"]);

    private static AlvoEvent Event(
        string type,
        AlvoRecord? record = null,
        AlvoRecord? oldRecord = null,
        IReadOnlyList<string>? changed = null,
        string? authId = null) => new()
        {
            Id = Guid.Parse("019000aa-0000-7000-8000-0000000000d1"),
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
            Subject = "deals/019000aa-0000-7000-8000-0000000000ff",
            PartitionKey = "deals:019000aa-0000-7000-8000-0000000000ff",
            AuthType = AlvoEventAuthType.ApiKey,
            AuthId = authId,
            CorrelationId = "019000aa-0000-7000-8000-0000000000c0",
            Data = new AlvoEventData
            {
                Record = record ?? AlvoRecord.Empty,
                OldRecord = oldRecord,
                Changed = changed ?? [],
            },
        };

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));

    /// <summary>
    /// An evaluator that fails on every expression — the only way to reach the fail-closed arm, since a condition
    /// compiled at apply time cannot fail on an author's mistake.
    /// </summary>
    private sealed class ThrowingEvaluator : IPredicateEvaluator
    {
        public bool Evaluate(
            CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context) =>
            throw new InvalidOperationException("the evaluator broke");
    }
}
