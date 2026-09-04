using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// What a <c>before*</c> hook does to a <b>real write</b>: a <c>mutate</c> reaching the stored row, a
/// <c>reject</c> refusing the write and leaving nothing behind, and an idempotent replay running no hook at
/// all — asked identically of every engine that ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own suite rather than facts added to <see cref="AlvoDataAdversarialTests"/></b>, for the reason
/// <see cref="AlvoDataConstraintTests"/> gives: that suite is inherited by the in-memory reference too, which
/// runs no hook pipeline, so a fact placed there would either be vacuous for it or demand it grow one. Both
/// shipped relational drivers inherit this one unchanged — §0 principle 3 is precisely that a rule-engine
/// behaviour is the same behaviour on every engine, and the only way to know is to ask both.
/// </para>
/// <para>
/// <b>What this suite deliberately cannot prove, stated rather than implied.</b> "The hook runs inside the
/// transaction" is not observable from here, and pretending otherwise would be the worse error. A hook runs
/// <em>before</em> the row is written — that is what lets it patch the candidate — so at the moment a
/// <c>reject</c> fires there is nothing yet written for the rollback to undo, on any of the four write sites.
/// The refusal therefore leaves no row whether the pipeline sits inside the transaction or just outside it,
/// and a fact asserting "no row" would stay green under exactly the mutation it looks like it guards against.
/// The transaction placement is pinned structurally instead, where it is genuinely visible —
/// <c>BeforeHookTransactionArchitectureTests</c> in the driver's own test project — and what this suite pins
/// is everything the placement was chosen to make possible.
/// </para>
/// <para>
/// <b>The subclass supplies a store and its hook counter, and nothing else.</b> The entity, the hooks, the
/// payloads and every assertion live here, so a fact cannot be weakened to make one engine pass.
/// </para>
/// </remarks>
public abstract class AlvoDataBeforeHookTests
{
    /// <summary>
    /// Builds a fresh store over <paramref name="descriptor"/>/<paramref name="schema"/>, together with the
    /// count of before-hook invocations its writes make.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose hooks, rules and field flags apply.</param>
    /// <param name="time">
    /// The clock every write is stamped from. Fixed rather than the system one, because two facts here compare
    /// a value a hook's <c>now()</c> produced with the audit column the same write recorded, and "the same
    /// instant" is only a question worth asking when the test chose the instant.
    /// </param>
    protected abstract Task<IAlvoDataBeforeHookWorld> WorldAsync(
        SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time);

    /// <summary>The instant every write in this suite is stamped with, and therefore what <c>now()</c> answers.</summary>
    protected static DateTimeOffset Stamp { get; } = new(2026, 8, 4, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The value a <c>mutate</c> produced is the value the database holds — the whole feature, on the create
    /// face.
    /// </summary>
    [Fact]
    public async Task A_mutate_reaches_the_stored_row()
    {
        var world = await DealsWorldAsync();

        var created = await CreateDealAsync(world, title: "BIG Deal");

        (await StoredAsync(world, created))["title"].ShouldBe(
            "big deal", "the hook folded the caller's title and the fold is what was stored");
    }

    /// <summary>
    /// <c>now()</c> inside a <c>mutate</c> is the write's <b>own</b> instant — the very one the audit stamp
    /// recorded — and not a second clock read that merely lands nearby.
    /// </summary>
    /// <remarks>
    /// Asserted as equality against <c>created_at</c> rather than against the test's own constant, because the
    /// stored representation is the authoritative one: a timestamp is normalised on the way in, so comparing
    /// the two stored columns asks "did one write produce one instant" without also asking "does this engine
    /// keep microseconds".
    /// </remarks>
    [Fact]
    public async Task A_mutate_reading_now_records_the_writes_own_audit_instant()
    {
        var world = await DealsWorldAsync();

        var created = await CreateDealAsync(world, title: "one instant");

        var stored = await StoredAsync(world, created);
        stored["approved_at"].ShouldBe(stored["created_at"]);
    }

    /// <summary>
    /// A mutated value reaches its column as a <b>bound parameter</b>, exactly like a caller's own value: a
    /// hook's output carrying quotes and a statement terminator is stored verbatim and composes no SQL.
    /// </summary>
    /// <remarks>
    /// The counterweight is the second create: a payload that had been interpolated into a statement would
    /// have left the table gone, so a suite that only compared the stored string could pass over the damage.
    /// </remarks>
    [Fact]
    public async Task A_mutate_writes_through_a_bound_parameter_like_every_other_value()
    {
        var world = await DealsWorldAsync();

        var created = await CreateDealAsync(world, title: "O'Brien'); DROP TABLE deals;--");

        (await StoredAsync(world, created))["title"].ShouldBe("o'brien'); drop table deals;--");
        await CreateDealAsync(world, title: "the table is still there");
    }

    /// <summary>
    /// A <c>reject</c> refuses the write, carrying the author's own text, and leaves no row behind.
    /// </summary>
    /// <remarks>
    /// The refusal's text is asserted because it is the RFC 7807 <c>detail</c> a caller reads, and it is
    /// descriptor-authored — the only kind of text this framework will echo back.
    /// </remarks>
    [Fact]
    public async Task A_reject_refuses_the_create_and_stores_no_row()
    {
        var world = await DealsWorldAsync();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => CreateDealAsync(world, title: "blocked deal", stage: Blocked));

        refusal.Message.ShouldContain(BlockedCreateRefusal);
        (await VisibleAsync(world)).ShouldBeEmpty("a refused create must leave nothing behind");
    }

    /// <summary>
    /// A <c>reject</c> inside a <b>batch</b> refuses that row by its own index, rather than aborting the
    /// whole batch anonymously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoBatchResult.Refusals"/> promises that every refused row is named. A hook's
    /// <c>reject</c> raises <see cref="AlvoAuthorizationException"/>, which is how a single write reports it
    /// — and left to propagate out of a batch's judging pass it would give the caller a bare refusal with
    /// nothing to repair, on a request where the whole point of the refusal list is that they can repair it.
    /// </para>
    /// <para>
    /// The control is the neighbouring row: it is a create the hook admits, so "refused" cannot pass because
    /// the batch path refuses everything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reject_inside_a_batch_names_the_row_it_refused()
    {
        var world = await DealsWorldAsync();

        var result = await world.Data.CreateManyAsync(
            Deals,
            [
                Payload(("tenant_id", Caller.Tenant!.Value.Value), ("title", "ordinary deal"), ("stage", "lead")),
                Payload(("tenant_id", Caller.Tenant!.Value.Value), ("title", "blocked deal"), ("stage", Blocked)),
            ],
            Caller,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        var refusal = result.Refusals.ShouldHaveSingleItem();
        refusal.Index.ShouldBe(1, "the row the hook refused, not the whole batch");
        refusal.Message.ShouldContain(BlockedCreateRefusal, customMessage: "the author's own text reaches the caller");
        (await VisibleAsync(world)).ShouldBeEmpty("a refused batch leaves nothing behind");
    }

    /// <summary>
    /// The counterweight to the refusal: a create whose <c>reject</c> condition is false is not refused. An
    /// implementation that refused every write would satisfy the fact above on its own.
    /// </summary>
    [Fact]
    public async Task A_reject_whose_condition_is_false_lets_the_create_through()
    {
        var world = await DealsWorldAsync();

        await CreateDealAsync(world, title: "ordinary deal");

        (await VisibleAsync(world)).Count.ShouldBe(1);
    }

    /// <summary>
    /// A <c>mutate</c> on the update face reaches the stored row, and its <c>now()</c> is that write's own
    /// instant — the update's <c>updated_at</c>, not the create's <c>created_at</c>.
    /// </summary>
    /// <remarks>
    /// The hook is gated on <c>changed(stage) &amp;&amp; new.stage == 'won'</c>, so this also pins that a
    /// before-hook condition can read <em>both</em> row images: the pre-image it compares against is the
    /// in-transaction, row-locked one.
    /// </remarks>
    [Fact]
    public async Task A_mutate_on_an_update_reaches_the_stored_row_at_that_writes_instant()
    {
        var world = await LateClockWorldAsync();
        var created = await CreateDealAsync(world, title: "to be won");

        await UpdateStageAsync(world, created, Won);

        var stored = await StoredAsync(world, created);
        stored["closed_at"].ShouldBe(stored["updated_at"]);
        stored["closed_at"].ShouldNotBe(stored["created_at"], "the update's instant, not the create's");
    }

    /// <summary>
    /// A <c>reject</c> on the update face refuses the write and leaves the stored row exactly as it was — no
    /// partially applied patch, not even the audit stamp the write would have carried.
    /// </summary>
    [Fact]
    public async Task A_reject_on_an_update_leaves_the_stored_row_untouched()
    {
        var world = await DealsWorldAsync();
        var created = await CreateDealAsync(world, title: "won deal");
        await UpdateStageAsync(world, created, Won);
        var before = await StoredAsync(world, created);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => UpdateStageAsync(world, created, "lost"));

        refusal.Message.ShouldContain(FrozenUpdateRefusal);
        (await StoredAsync(world, created)).Values.ShouldBe(before.Values);
    }

    /// <summary>A <c>reject</c> on the delete face refuses the delete and the row is still there.</summary>
    [Fact]
    public async Task A_reject_on_a_delete_leaves_the_row_in_place()
    {
        var world = await DealsWorldAsync();
        var created = await CreateDealAsync(world, title: "won deal");
        await UpdateStageAsync(world, created, Won);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.DeleteAsync(Deals, IdOf(created), Caller, cancellationToken: Ct));

        refusal.Message.ShouldContain(WonDeleteRefusal);
        (await world.Data.GetAsync(Deals, IdOf(created), Caller, Ct)).ShouldNotBeNull();
    }

    /// <summary>
    /// The delete counterweight: a row no hook refuses still deletes. Without it, an implementation that
    /// refused every delete would satisfy the fact above.
    /// </summary>
    [Fact]
    public async Task A_delete_no_hook_refuses_still_removes_the_row()
    {
        var world = await DealsWorldAsync();
        var created = await CreateDealAsync(world, title: "ordinary deal");

        await world.Data.DeleteAsync(Deals, IdOf(created), Caller, cancellationToken: Ct);

        (await world.Data.GetAsync(Deals, IdOf(created), Caller, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// A hook may write a field the <em>caller</em> is refused, and that is the ruling this suite's half of the
    /// DoD asks for: <c>WritePayloadGuard</c> judges a caller's keys and does not re-run over a hook's patch.
    /// </summary>
    /// <remarks>
    /// Both directions in one fact, because either alone proves nothing. The caller's own attempt to write
    /// <c>code</c> is refused, so <c>readOnly</c> demonstrably <em>is</em> enforced; the hook's patch of the
    /// same field lands, so the guard demonstrably does not re-run over it.
    /// </remarks>
    [Fact]
    public async Task A_mutate_may_write_a_field_the_caller_is_refused()
    {
        var world = await DealsWorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.CreateAsync(
            Deals, Payload(("title", "mine"), ("code", "MINE")), Caller, cancellationToken: Ct));

        var created = await CreateDealAsync(world, title: "not mine");
        (await StoredAsync(world, created))["code"].ShouldBe(AssignedCode);
    }

    /// <summary>
    /// <b>An idempotent replay runs no hook.</b> The hooks ran on the write that produced the recorded row, so
    /// running them again would apply a second <c>mutate</c> over a stored value and would let a
    /// <c>reject</c> refuse a retry of a create the caller was already told succeeded.
    /// </summary>
    /// <remarks>
    /// Counted rather than inferred from the row, and that is the whole reason
    /// <see cref="IAlvoDataBeforeHookWorld.HookRuns"/> exists: a before-hook is pure, so a second run over the
    /// same candidate computes the same value and the answer a replay returns is identical either way. The
    /// count is the only observable difference there is.
    /// </remarks>
    [Fact]
    public async Task An_idempotent_replay_runs_no_hook_a_second_time()
    {
        var world = await DealsWorldAsync();
        var token = new AlvoIdempotency("k-1", $"{Deals}:one-request-digest");
        var created = await CreateDealAsync(world, title: "BIG Deal", idempotency: token);

        var replayed = await CreateDealAsync(world, title: "BIG Deal", idempotency: token);

        IdOf(replayed).ShouldBe(IdOf(created), "a replay answers with the recorded row");
        world.HookRuns.ShouldBe([DataOperation.Create], "the hook ran on the write, and the replay is not one");
    }

    /// <summary>
    /// All three write faces consult the pipeline, so no face can silently stop running hooks while the other
    /// two keep the suite green.
    /// </summary>
    [Fact]
    public async Task Every_write_face_consults_the_hook_pipeline()
    {
        var world = await DealsWorldAsync();
        var created = await CreateDealAsync(world, title: "ordinary deal");
        await UpdateStageAsync(world, created, "offer");
        await world.Data.DeleteAsync(Deals, IdOf(created), Caller, cancellationToken: Ct);

        world.HookRuns.ShouldBe([DataOperation.Create, DataOperation.Update, DataOperation.Delete]);
    }

    /// <summary>
    /// <b>A patch is judged by the write's own policy, and this is the bypass that makes it necessary.</b> The
    /// caller passes the <c>create</c> rule with their own id, and the hook then rewrites the very field the
    /// rule is about — from a field the caller controls. The verdict is re-reached over the patched post-image,
    /// so the write is refused.
    /// </summary>
    /// <remarks>
    /// The candidate's first verdict is reached before the transaction opens, over the caller's own payload; the
    /// patch happens after it. Without the second verdict the row would be stored owned by whoever the caller
    /// named — a row the <c>create</c> rule refuses, written through a hook. The counterweight below is what
    /// keeps this from being satisfied by refusing every patched write.
    /// </remarks>
    [Fact]
    public async Task A_mutate_that_moves_a_row_out_of_the_create_rule_is_refused()
    {
        var world = await DealsWorldAsync();
        var stranger = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.CreateAsync(
            Guarded,
            Payload(("owner_id", Caller.User.Value), ("requested_owner", stranger)),
            Caller,
            cancellationToken: Ct));

        (await world.Data.QueryAsync(new AlvoQuery { Entity = Guarded }, Caller, Ct)).Items.ShouldBeEmpty(
            "the refused write must leave no row owned by whoever the caller named");
    }

    /// <summary>
    /// The counterweight: a patch the rule accepts still lands. Without it, an implementation that refused
    /// every patched write would satisfy the fact above.
    /// </summary>
    [Fact]
    public async Task A_mutate_the_create_rule_accepts_still_lands()
    {
        var world = await DealsWorldAsync();

        var created = await world.Data.CreateAsync(
            Guarded,
            Payload(("owner_id", Caller.User.Value), ("requested_owner", Caller.User.Value)),
            Caller,
            cancellationToken: Ct);

        created["owner_id"].ShouldBe(Caller.User.Value);
    }

    /// <summary>
    /// <b>A hook cannot patch a row past the check inside a batch either.</b> The batch's judging pass runs
    /// the hooks and re-judges what they produced; without that second verdict a row the <c>create</c> rule
    /// refuses would be stored, written through a hook, once per batch row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <see cref="A_mutate_that_moves_a_row_out_of_the_create_rule_is_refused"/> at batch scale, and
    /// it needs its own fact because the batch's re-verdict is a <em>second copy</em> of that closer —
    /// <c>EfAlvoData.RunBeforeCreateOrRefuse</c> — living on a different path. Delete it and the single-row
    /// fact stays green.
    /// </para>
    /// <para>
    /// The good row beside the bad one is the control: it proves the batch was refused for the patched row
    /// rather than because the batch path refuses everything, and the count proves it wrote nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_mutate_cannot_move_a_row_out_of_the_create_rule_inside_a_batch()
    {
        var world = await DealsWorldAsync();
        var stranger = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

        var result = await world.Data.CreateManyAsync(
            Guarded,
            [
                Payload(("owner_id", Caller.User.Value), ("requested_owner", Caller.User.Value)),
                Payload(("owner_id", Caller.User.Value), ("requested_owner", stranger)),
            ],
            Caller,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse("the hook moved row 1 out of the create rule");
        result.Refusals.Select(refusal => refusal.Index).ShouldBe([1]);
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Guarded }, Caller, Ct)).Items.ShouldBeEmpty(
            "and a refused batch leaves neither row behind");
    }

    /// <summary>
    /// The counterweight: a batch whose patches the rule accepts still lands, so the fact above cannot pass
    /// on a batch path that refuses every patched write.
    /// </summary>
    [Fact]
    public async Task A_batch_whose_mutates_the_create_rule_accepts_still_lands()
    {
        var world = await DealsWorldAsync();

        var result = await world.Data.CreateManyAsync(
            Guarded,
            [
                Payload(("owner_id", Caller.User.Value), ("requested_owner", Caller.User.Value)),
                Payload(("owner_id", Caller.User.Value), ("requested_owner", Caller.User.Value)),
            ],
            Caller,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        result.Rows.Count.ShouldBe(2);
    }

    /// <summary>
    /// <b>The row that lands is the row that was judged.</b> The hook runs in the judging pass, and what is
    /// stored is what that verdict consumed rather than something a write pass re-derived.
    /// </summary>
    /// <remarks>
    /// The <c>mutate</c> on this fixture lower-cases the title and assigns a code the caller may not write.
    /// Reading both back off every stored row is what says the judged image is the stored one: a write pass
    /// that re-derived the row from the caller's payload would store the caller's casing and no code at all.
    /// </remarks>
    [Fact]
    public async Task Every_row_a_batch_stores_is_the_row_its_hook_produced()
    {
        var world = await DealsWorldAsync();

        var result = await world.Data.CreateManyAsync(
            Deals,
            [
                Payload(("tenant_id", Caller.Tenant!.Value.Value), ("title", "FIRST"), ("stage", "lead")),
                Payload(("tenant_id", Caller.Tenant!.Value.Value), ("title", "SECOND"), ("stage", "lead")),
            ],
            Caller,
            cancellationToken: Ct);

        result.Rows.Count.ShouldBe(2);
        foreach (var row in result.Rows)
        {
            row["code"].ShouldBe(AssignedCode, "the hook's own value, on every row");
        }

        (await VisibleAsync(world)).Select(row => row["title"]).ShouldBe(
            ["first", "second"],
            ignoreOrder: true,
            customMessage: "the stored title is the hook's output, not the caller's casing");
    }

    private const string Deals = "deals";

    /// <summary>
    /// The second entity, whose whole purpose is one adversarial question: its <c>create</c> rule is about the
    /// very field its hook patches, from a field the caller controls.
    /// </summary>
    private const string Guarded = "guarded";

    private const string Blocked = "blocked";

    private const string Won = "won";

    /// <summary>The value the create hook assigns to <c>code</c>, a field callers may not write.</summary>
    private const string AssignedCode = "AUTO-1";

    private const string BlockedCreateRefusal = "A blocked deal cannot be created.";

    private const string FrozenUpdateRefusal = "A won deal is frozen.";

    private const string WonDeleteRefusal = "A won deal cannot be deleted.";

    private static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };

    private Task<IAlvoDataBeforeHookWorld> DealsWorldAsync() => WorldAsync(Schema, Descriptor, new FixedClock(Stamp));

    /// <summary>
    /// A store whose clock is one minute later than <see cref="Stamp"/>, so an update's own instant is
    /// distinguishable from the create's. A fixed clock is what makes that a decision rather than a race.
    /// </summary>
    private Task<IAlvoDataBeforeHookWorld> LateClockWorldAsync() =>
        WorldAsync(Schema, Descriptor, new AdvancingClock(Stamp, TimeSpan.FromMinutes(1)));

    private static Task<AlvoRecord> CreateDealAsync(
        IAlvoDataBeforeHookWorld world, string title, string stage = "lead", AlvoIdempotency? idempotency = null) =>
        world.Data.CreateAsync(
            Deals,
            Payload(("tenant_id", Caller.Tenant!.Value.Value), ("title", title), ("stage", stage)),
            Caller,
            idempotency,
            Ct);

    private static Task<AlvoRecord> UpdateStageAsync(
        IAlvoDataBeforeHookWorld world, AlvoRecord deal, string stage) =>
        world.Data.UpdateAsync(Deals, IdOf(deal), Payload(("stage", stage)), Caller, cancellationToken: Ct);

    private static async Task<AlvoRecord> StoredAsync(IAlvoDataBeforeHookWorld world, AlvoRecord deal) =>
        await world.Data.GetAsync(Deals, IdOf(deal), Caller, Ct) ?? throw new InvalidOperationException(
            "The row this suite just wrote could not be read back, so no fact about its stored values is asked.");

    private static async Task<IReadOnlyList<AlvoRecord>> VisibleAsync(IAlvoDataBeforeHookWorld world) =>
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Deals }, Caller, Ct)).Items;

    private static Guid IdOf(AlvoRecord record) => (Guid)record["id"]!;

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] values) =>
        values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>One instant, for every read. The write path normalises it, so the store decides the precision.</summary>
    /// <param name="instant">The instant every read of this clock answers.</param>
    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    /// <summary>
    /// A clock that answers <paramref name="start"/> once and then moves on by <paramref name="step"/>, so a
    /// create and the update that follows it are stamped with two instants a fact can tell apart.
    /// </summary>
    /// <param name="start">The instant the first read answers.</param>
    /// <param name="step">How far the clock moves after every read.</param>
    private sealed class AdvancingClock(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now = _now.Add(step);
            return now;
        }
    }

    /// <summary>
    /// One entity with one hook per point: a create that folds and stamps, a create that refuses, an update
    /// that stamps on the transition, an update that freezes a won deal, and a delete that protects one.
    /// </summary>
    private static EntityHooks Hooks => new()
    {
        BeforeCreate =
        [
            new BeforeHook
            {
                Action = new BeforeHookAction
                {
                    Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal)
                    {
                        ["title"] = ValueOrExpr.FromExpression("lowerAscii(new.title)"),
                        ["approved_at"] = ValueOrExpr.FromExpression("now()"),
                        ["code"] = Text(AssignedCode),
                    },
                },
            },
            new BeforeHook
            {
                Condition = $"new.stage == '{Blocked}'",
                Action = new BeforeHookAction { Reject = BlockedCreateRefusal },
            },
        ],
        BeforeUpdate =
        [
            new BeforeHook
            {
                Condition = $"old.stage == '{Won}'",
                Action = new BeforeHookAction { Reject = FrozenUpdateRefusal },
            },
            new BeforeHook
            {
                Condition = $"changed(stage) && new.stage == '{Won}'",
                Action = new BeforeHookAction
                {
                    Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal)
                    {
                        ["closed_at"] = ValueOrExpr.FromExpression("now()"),
                    },
                },
            },
        ],
        BeforeDelete =
        [
            new BeforeHook
            {
                Condition = $"old.stage == '{Won}'",
                Action = new BeforeHookAction { Reject = WonDeleteRefusal },
            },
        ],
    };

    /// <summary>A JSON string literal, in the shape <see cref="ValueOrExpr.FromLiteral"/> takes it.</summary>
    /// <param name="value">The string the literal carries.</param>
    private static ValueOrExpr Text(string value) =>
        ValueOrExpr.FromLiteral(System.Text.Json.JsonDocument.Parse($"\"{value}\"").RootElement);

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "before-hook-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Deals] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Audit = true,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String, Required = true },
                    ["stage"] = new() { Type = DescField.String },

                    // Frozen for callers and written by the create hook: the two halves of the readOnly ruling.
                    ["code"] = new() { Type = DescField.String, ReadOnly = BoolOrCel.FromBoolean(true) },
                    ["approved_at"] = new() { Type = DescField.DateTime },
                    ["closed_at"] = new() { Type = DescField.DateTime },
                },
                Rules = AllowAll,
                Hooks = Hooks,
            },
            [Guarded] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["owner_id"] = new() { Type = DescField.Uuid },
                    ["requested_owner"] = new() { Type = DescField.Uuid },
                },
                Rules = AllowAll with { Create = "owner_id == @user.id" },
                Hooks = new EntityHooks
                {
                    BeforeCreate =
                    [
                        new BeforeHook
                        {
                            Action = new BeforeHookAction
                            {
                                Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal)
                                {
                                    ["owner_id"] = ValueOrExpr.FromExpression("new.requested_owner"),
                                },
                            },
                        },
                    ],
                },
            },
        },
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <see cref="AlvoDataConstraintTests"/> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from this project.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Deals,
            Tenancy = TenancyMode.Scoped,
            Audit = true,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 200 },
                new FieldSchema { Name = "stage", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
                new FieldSchema { Name = "code", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
                new FieldSchema { Name = "approved_at", Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema { Name = "closed_at", Type = SchemaField.DateTime, Nullable = true },

                // Last, exactly as the core's mapper appends its managed columns.
                new FieldSchema
                {
                    Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true,
                },
                new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
            ],
        },
        new EntitySchema
        {
            Name = Guarded,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "owner_id", Type = SchemaField.Uuid, Nullable = true },
                new FieldSchema { Name = "requested_owner", Type = SchemaField.Uuid, Nullable = true },
            ],
        },
    ]);
}
