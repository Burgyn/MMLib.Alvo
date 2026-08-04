using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

using Shouldly;

using Xunit;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// Two of <c>baas-analyza.md:676-680</c>'s acceptance criteria, end to end over a real write path and a real
/// dispatcher: an approval transition fires <b>exactly once, at the transition</b>, and events that match
/// nothing produce <b>no execution-log entry and one counter increment each</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The subclass supplies a started database and nothing else.</b> The entity, the hooks, the payloads and
/// every assertion live here, so a criterion cannot be weakened per engine — the arrangement every other
/// inherited suite in this library uses. Both shipped relational drivers inherit it unchanged, which is what
/// makes "identical on SQLite and PostgreSQL" (§0 principle 3) a measurement rather than a claim.
/// </para>
/// <para>
/// <b>Every fact here carries its own positive control, in the same run.</b> An assertion about absence — no
/// second delivery, no execution-log entry — passes just as well when nothing ran at all, when the condition
/// never compiled, or when no event was ever written. So each fact also drives one event that <em>does</em>
/// match, asserts the outbox held the rows before the drain, and asserts they are retired after it. The
/// counters are asserted by <em>value</em> for the same reason.
/// </para>
/// <para>
/// <b>The entity declares two after-hooks, and the second one is load-bearing.</b> Only one of them can match
/// any event here, so "one counter increment per event" is measurably different from "one per hook": an
/// entity with two non-matching hooks would otherwise report a filtered event twice, and a suite with a
/// single hook could not tell the two apart.
/// </para>
/// </remarks>
public abstract class AlvoEventCriteriaTests
{
    /// <summary>
    /// Builds a fresh backend over <see cref="Project"/>: a migrated database, a primed policy catalog, a
    /// recording webhook receiver, a recording log and a listener on the event meter.
    /// </summary>
    protected abstract Task<IAlvoEventWorld> WorldAsync();

    /// <summary>
    /// <c>baas-analyza.md:677</c>: <c>changed(status) &amp;&amp; new.status == 'approved'</c> fires
    /// <b>exactly once, at the transition</b>.
    /// </summary>
    /// <remarks>
    /// The second update is the fact. A hook that fired on every write to an already-approved row would
    /// satisfy a bare "fired at least once" assertion perfectly, and that is the shape the criterion exists to
    /// rule out — <c>changed(status)</c> must be false when the value did not move, which is only true if the
    /// envelope's <c>old_record</c> is the row's own pre-image. The first delivery is the positive control:
    /// without it, a build that delivered nothing at all would pass the second assertion.
    /// </remarks>
    [Fact]
    public async Task An_approval_transition_fires_exactly_once_and_a_second_approval_does_not()
    {
        await using var world = await WorldAsync();
        var row = await world.CreateAsync(status: Draft);

        await world.UpdateAsync(row, status: Approved);
        await world.DrainAsync();

        var delivered = world.Deliveries.ShouldHaveSingleItem();
        delivered.Url.ShouldBe(ApprovalEndpointUrl);
        delivered.Body.ShouldContain(UpdatedEventType);
        delivered.Body.ShouldContain(row.ToString(), Case.Insensitive);

        await world.UpdateAsync(row, status: Approved, plate: "AFTER-APPROVAL");
        await world.DrainAsync();

        world.Deliveries.Count.ShouldBe(
            1,
            "changed(status) must be false when status did not move; a second delivery means the condition was "
            + "evaluated against a pre-image that was not the row's own");
        world.Metrics.CountOf(DispatchedCounter).ShouldBe(1);
        world.Metrics.CountOf(FilteredCounter).ShouldBe(
            2, "the create and the second update each matched nothing, and each counts once");
        (await world.TallyAsync()).ShouldBe(
            new AlvoOutboxTally(Pending: 0, Retired: 3),
            "three writes queued three events and the pump retired all three, so the absence above is about "
            + "the condition rather than about events that were never written or never claimed");
    }

    /// <summary>
    /// A transition to a value the condition does not name does not fire — and the wiring is proved live in
    /// the same run by an approval that does.
    /// </summary>
    /// <remarks>
    /// This is the second conjunct's own fact: <c>new.status == 'approved'</c> is false for a rejection even
    /// though <c>changed(status)</c> is true, so a build that ignored the value and fired on any status change
    /// fails here while passing the transition fact above.
    /// </remarks>
    [Fact]
    public async Task A_transition_to_a_value_the_condition_does_not_name_does_not_fire()
    {
        await using var world = await WorldAsync();
        var row = await world.CreateAsync(status: Draft);

        await world.UpdateAsync(row, status: Rejected);
        await world.DrainAsync();

        world.Deliveries.ShouldBeEmpty("changed(status) holds for a rejection, but new.status == 'approved' does not");

        await world.UpdateAsync(row, status: Approved);
        await world.DrainAsync();

        world.Deliveries.ShouldHaveSingleItem().Url.ShouldBe(
            ApprovalEndpointUrl,
            "the same world delivers an approval, so the absence above is a decision the condition made rather "
            + "than a delivery path that never worked");
        (await world.TallyAsync()).ShouldBe(new AlvoOutboxTally(Pending: 0, Retired: 3));
    }

    /// <summary>
    /// <c>baas-analyza.md:678</c>: N events matching nothing produce <b>zero execution-log rows and one
    /// counter increment</b> each — measured beside one event that matches, in the same run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §3.3 records the consequence of getting this wrong as a documented Directus defect — thousands of log
    /// entries for runs that abort immediately on their condition, making debugging impossible. Confirmed from
    /// Directus source: <c>api/src/flows.ts</c> subscribes with no predicate and the activity/revision write
    /// happens <em>after</em> the operation loop, so a flow that dies on its first condition still writes one
    /// activity row and one revision row; per-item fan-out then multiplies it, so 10 000 inserts are 10 000
    /// runs and 10 000 rows.
    /// </para>
    /// <para>
    /// In F3 the "execution log" is one structured entry per <em>executed action</em> plus three metrics
    /// counters, not a table — a durable queryable log with retention and a redelivery UI is 7.1 (plan
    /// decision D6). The criterion is unchanged by that: a filtered event costs one counter increment and no
    /// action entry.
    /// </para>
    /// <para>
    /// <b>The N non-matching events are updates, not creates.</b> They reach the very hook point that carries
    /// the condition and are turned away by the condition alone — which is what makes this fact about the
    /// <em>subscription</em> step. Events that matched no hook point at all would be filtered by a build that
    /// evaluated its conditions inside the action, so the Directus defect would pass straight through them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_hundred_events_matching_nothing_produce_no_action_log_and_one_counter_each()
    {
        await using var world = await WorldAsync();
        var row = await world.CreateAsync(status: Draft);
        foreach (var index in Enumerable.Range(0, NonMatchingUpdates))
        {
            await world.UpdateAsync(row, plate: $"PLATE-{index:D4}");
        }

        await world.UpdateAsync(row, status: Approved);
        (await world.TallyAsync()).ShouldBe(
            new AlvoOutboxTally(Pending: QueuedEvents, Retired: 0),
            $"the {QueuedEvents} events have to exist before their absence from the execution log means anything");

        await world.DrainAsync();

        world.ActionLogEntries.ShouldHaveSingleItem(
                $"{NonMatchingEvents} filtered events must produce no execution-log entry at all — this is the "
                + "documented Directus defect §3.3 cites, and Alvo avoids it by construction because the CEL "
                + "Condition profile is compiled at apply time and evaluated at subscription time — while the "
                + "one event that matched must produce exactly one")
            .Message.ShouldContain(ApprovalHookPointer);
        world.Metrics.CountOf(FilteredCounter).ShouldBe(
            NonMatchingEvents, "one increment per filtered event, never one per hook the event did not match");
        world.Metrics.CountOf(DispatchedCounter).ShouldBe(MatchingEvents);
        world.Deliveries.ShouldHaveSingleItem();
        (await world.TallyAsync()).ShouldBe(
            new AlvoOutboxTally(Pending: 0, Retired: QueuedEvents),
            "every event was claimed and retired, so the counts above are over a queue the pump really drained");
    }

    /// <summary>
    /// A matched event writes exactly one execution-log entry, naming the hook that ran and the action type.
    /// </summary>
    /// <remarks>
    /// The other half of the criterion: the log is not empty by being unwritten. The entry names the hook's own
    /// JSON pointer and the event's type, so an entry that named the entity or nothing at all fails — a log line
    /// an operator cannot trace back to a descriptor position and an outbox row is the debugging problem the
    /// criterion is about. It deliberately does not carry a value out of the record; that is the executor's own
    /// decision, and the event id is the join key to the payload instead.
    /// </remarks>
    [Fact]
    public async Task A_matched_event_writes_exactly_one_action_log_entry_naming_the_hook()
    {
        await using var world = await WorldAsync();
        var row = await world.CreateAsync(status: Draft);

        await world.UpdateAsync(row, status: Approved);
        await world.DrainAsync();

        var entry = world.ActionLogEntries.ShouldHaveSingleItem();
        entry.Message.ShouldContain(ApprovalHookPointer);
        entry.Message.ShouldContain(WebhookActionType);
        entry.Message.ShouldContain(UpdatedEventType);
        world.Metrics.CountOf(DispatchedCounter).ShouldBe(1);
    }

    /// <summary>
    /// The project every fact here is measured against, and the one a subclass stands its database up from.
    /// </summary>
    /// <remarks>
    /// Composed on each read rather than cached in a static initializer, because a static field initializer
    /// runs in declaration order and would capture the members below before they were assigned.
    /// </remarks>
    protected static AlvoEventProject Project => new(Schema, Descriptor, Caller, Entity, EventMeterName);

    /// <summary>
    /// The meter the event counters are published on.
    /// </summary>
    /// <remarks>
    /// Restated here because the core's <c>AlvoEventMetrics</c> is <see langword="internal"/> and this library
    /// depends on <c>MMLib.Alvo.Abstractions</c> alone. Drift is fail-loud rather than silent: every one of
    /// these three names is asserted somewhere below with a <em>non-zero</em> expected count, and a listener on
    /// a meter nobody publishes — or an instrument nobody named — answers zero.
    /// </remarks>
    protected const string EventMeterName = "MMLib.Alvo.Events";

    /// <inheritdoc cref="EventMeterName"/>
    protected const string DispatchedCounter = "alvo.events.dispatched";

    /// <inheritdoc cref="EventMeterName"/>
    protected const string FilteredCounter = "alvo.events.filtered";

    /// <summary>The entity every write here targets.</summary>
    protected const string Entity = "vehicles";

    private const string Draft = "draft";
    private const string Approved = "approved";
    private const string Rejected = "rejected";
    private const string Archived = "archived";

    private const string UpdatedEventType = $"entity.{Entity}.updated";
    private const string ApprovalHookPointer = $"/entities/{Entity}/hooks/afterUpdate/0";
    private const string WebhookActionType = "webhook";

    private const string ApprovalEndpoint = "approval-sync";
    private const string ApprovalEndpointUrl = "https://receiver.test/approvals";
    private const string ArchiveEndpoint = "archive-sync";
    private const string ArchiveEndpointUrl = "https://receiver.test/archives";

    /// <summary>How many events reach the hook point and are turned away by the condition alone.</summary>
    private const int NonMatchingUpdates = 100;

    /// <summary>The create that seeded the row is itself an event, and it matches nothing either.</summary>
    private const int NonMatchingEvents = NonMatchingUpdates + 1;

    /// <summary>The same-run positive control: one approval, which matches exactly one of the two hooks.</summary>
    private const int MatchingEvents = 1;

    private const int QueuedEvents = NonMatchingEvents + MatchingEvents;

    /// <summary>
    /// The caller every write here is performed as: authenticated, because the entity's rules require it.
    /// </summary>
    private static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("cccccccc-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>
    /// The descriptor: one entity, two <c>afterUpdate</c> hooks conditioned on two different transitions, and
    /// the two endpoints they post to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoints differ so a delivery says <em>which</em> hook produced it. With one shared URL a build
    /// that ran both hooks on one event would only be visible as a count, and a build that ran the wrong one
    /// would not be visible at all.
    /// </para>
    /// <para>
    /// Neither action declares a <c>payload</c>, so both deliver the canonical envelope — the shape a receiver
    /// gets by default, and the one whose <c>old_record</c> the transition condition was judged against.
    /// </para>
    /// </remarks>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "event-criteria-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["status"] = new() { Type = DescField.String },
                    ["plate"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly,
                    Get = AuthenticatedOnly,
                    Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly,
                    Delete = AuthenticatedOnly,
                },
                Hooks = new EntityHooks
                {
                    AfterUpdate =
                    [
                        TransitionHook(Approved, ApprovalEndpoint),
                        TransitionHook(Archived, ArchiveEndpoint),
                    ],
                },
            },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                [ApprovalEndpoint] = new() { Url = ApprovalEndpointUrl, SecretRef = "approval-sync-secret" },
                [ArchiveEndpoint] = new() { Url = ArchiveEndpointUrl, SecretRef = "archive-sync-secret" },
            },
        },
    };

    /// <summary>One hook that fires when <c>status</c> moves to <paramref name="status"/>, and never else.</summary>
    private static AfterHook TransitionHook(string status, string endpoint) => new()
    {
        Condition = $"changed(status) && new.status == '{status}'",
        Action = new WebhookAction { Endpoint = endpoint },
    };

    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <see cref="AlvoDataOutboxTests"/> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from this project.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "status", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = "plate", Type = SchemaField.String, Nullable = true },
            ],
        },
    ]);
}
