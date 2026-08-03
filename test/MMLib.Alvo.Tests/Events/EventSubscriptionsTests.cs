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
            .Matching(CatalogConditionedOnWinning, Updated("won", "lead"), new ThrowingEvaluator(), Context, _logger)
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
            CatalogConditionedOnWinning, @event, new ThrowingEvaluator(), Context, _logger).ShouldBeEmpty();

        var line = _logger.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Debug);
        line.Message.ShouldContain("/entities/deals/hooks/afterUpdate/0");
        line.Message.ShouldContain(@event.Id.ToString());
        line.Exception.ShouldNotBeNull();
    }

    private readonly CapturingLogger _logger = new();

    private static AlvoContext Context { get; } = AlvoContext.System(tenant: null);

    private IReadOnlyList<CompiledAfterHook> Matching(PolicyCatalog catalog, AlvoEvent @event) =>
        EventSubscriptions.Matching(catalog, @event, CelFixtures.Evaluator, Context, _logger);

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
        ],
    };

    private static AlvoEvent Updated(string stage, string was) => Event(
        "entity.deals.updated",
        record: Record(("stage", stage)),
        oldRecord: Record(("stage", was)),
        changed: string.Equals(stage, was, StringComparison.Ordinal) ? [] : ["stage"]);

    private static AlvoEvent Event(
        string type,
        AlvoRecord? record = null,
        AlvoRecord? oldRecord = null,
        IReadOnlyList<string>? changed = null) => new()
        {
            Id = Guid.Parse("019000aa-0000-7000-8000-0000000000d1"),
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
            Subject = "deals/019000aa-0000-7000-8000-0000000000ff",
            PartitionKey = "deals:019000aa-0000-7000-8000-0000000000ff",
            AuthType = AlvoEventAuthType.ApiKey,
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
