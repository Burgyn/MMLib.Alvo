using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The command ledger, the inbound webhook ledger and a command's writes inside a unit — the same facts on SQLite and on
/// PostgreSQL (spec 2026-10-14 §6.1: identical SQL, contract facts on both). Every claim is fenced (Ruling 36).
/// </summary>
public abstract class LedgerFacts
{
    private const string Entity = "notes";

    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _pastTheLease = CommandLedgerLimits.Lease + TimeSpan.FromSeconds(1);

    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor);

    [Fact]
    public async Task A_fresh_key_is_claimed_with_a_token_and_a_second_claim_finds_it_in_flight()
    {
        var ledger = await LedgerAsync();

        var first = await ledger.ClaimAsync(Key("k1"), _now, Ct);

        first.State.ShouldBe(CommandClaimState.Claimed);
        first.Token.ShouldNotBeNullOrWhiteSpace();
        (await ledger.ClaimAsync(Key("k1"), _now.AddSeconds(1), Ct)).State.ShouldBe(CommandClaimState.InFlight);
    }

    [Fact]
    public async Task A_key_spent_on_another_request_is_a_mismatch()
    {
        var ledger = await LedgerAsync();
        await ledger.ClaimAsync(Key("k2"), _now, Ct);

        (await ledger.ClaimAsync(Key("k2") with { Fingerprint = "other" }, _now, Ct)).State.ShouldBe(CommandClaimState.Mismatch);
    }

    [Fact]
    public async Task An_expired_uncommitted_claim_is_taken_over_with_a_new_token()
    {
        var ledger = await LedgerAsync();
        var first = await ledger.ClaimAsync(Key("k3"), _now, Ct);

        var second = await ledger.ClaimAsync(Key("k3"), _now + _pastTheLease, Ct);

        second.State.ShouldBe(CommandClaimState.Claimed);
        second.Token.ShouldNotBe(first.Token);
    }

    [Fact]
    public async Task A_released_claim_is_claimed_again_at_once()
    {
        var ledger = await LedgerAsync();
        var claim = await ledger.ClaimAsync(Key("k4"), _now, Ct);

        await ledger.ReleaseAsync(Held(Key("k4"), claim), Ct);

        (await ledger.ClaimAsync(Key("k4"), _now, Ct)).State.ShouldBe(CommandClaimState.Claimed);
    }

    [Fact]
    public async Task A_stale_release_does_not_free_a_live_claim()
    {
        var ledger = await LedgerAsync();
        var stale = await ledger.ClaimAsync(Key("k5"), _now, Ct);
        await ledger.ClaimAsync(Key("k5"), _now + _pastTheLease, Ct);

        await ledger.ReleaseAsync(Held(Key("k5"), stale), Ct);

        (await ledger.ClaimAsync(Key("k5"), _now + _pastTheLease, Ct)).State.ShouldBe(CommandClaimState.InFlight, "the newer run still holds it");
    }

    [Fact]
    public async Task A_stale_answer_changes_nothing_so_a_retry_never_replays_it()
    {
        var ledger = await LedgerAsync();
        var stale = await ledger.ClaimAsync(Key("k11"), _now, Ct);
        await ledger.ClaimAsync(Key("k11"), _now + _pastTheLease, Ct);

        await ledger.AnswerAsync(Held(Key("k11"), stale), new CommandAnswer(200, "{\"stale\":true}"), Ct);

        var retry = await ledger.ClaimAsync(Key("k11"), _now + _pastTheLease, Ct);
        retry.State.ShouldBe(CommandClaimState.InFlight, "the newer run still holds it");
        retry.Answer.ShouldBeNull();
    }

    [Fact]
    public async Task A_completion_without_a_unit_commits_the_event_and_the_answer_together()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var claim = await ledger.ClaimAsync(Key("k6"), _now, Ct);

        await ledger.CompleteAsync(Completion(), Held(Key("k6"), claim), new CommandAnswer(200, "{\"total\":3}"), _now, Ct);

        var replay = await ledger.ClaimAsync(Key("k6"), _now.AddHours(1), Ct);
        replay.State.ShouldBe(CommandClaimState.Answered);
        replay.Answer.ShouldBe(new CommandAnswer(200, "{\"total\":3}"));
        (await PendingTypesAsync(services)).ShouldContain("alvo.command.quote_stay.completed");
    }

    [Fact]
    public async Task A_problem_answer_keeps_its_media_type_for_the_replay()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var key = Held(Key("k15"), await ledger.ClaimAsync(Key("k15"), _now, Ct));
        var problem = new CommandAnswer(409, "{\"status\":409}", "application/problem+json");

        await ledger.CompleteAsync(Completion(), key, problem, _now, Ct);

        (await ledger.ClaimAsync(Key("k15"), _now.AddHours(1), Ct)).Answer.ShouldBe(problem);
    }

    [Fact]
    public async Task A_completion_by_a_run_that_lost_its_claim_is_refused_with_its_event()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var stale = await ledger.ClaimAsync(Key("k7"), _now, Ct);
        await ledger.ClaimAsync(Key("k7"), _now + _pastTheLease, Ct);

        await Should.ThrowAsync<CommandClaimLostException>(() =>
            ledger.CompleteAsync(Completion(), Held(Key("k7"), stale), new CommandAnswer(200, null), _now + _pastTheLease, Ct));

        (await PendingTypesAsync(services)).ShouldNotContain("alvo.command.quote_stay.completed", "the event rolled back with the refused commit");
    }

    [Fact]
    public async Task A_unit_that_commits_carries_the_event_and_the_commit_mark_and_an_answer_follows()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var key = Held(Key("k8"), await ledger.ClaimAsync(Key("k8"), _now, Ct));

        await services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "confirmed" }, ct);
            await ((IAlvoCommandUnit)unit).PublishFrameworkAsync(Completion(), ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, _now, ct);
        }, cancellationToken: Ct);

        (await ledger.ClaimAsync(Key("k8"), _now.AddHours(1), Ct)).State.ShouldBe(CommandClaimState.Unanswered);
        await ledger.AnswerAsync(key, new CommandAnswer(201, null), Ct);
        (await ledger.ClaimAsync(Key("k8"), _now.AddHours(1), Ct)).Answer.ShouldBe(new CommandAnswer(201, null));
        (await PendingTypesAsync(services)).ShouldContain("alvo.command.quote_stay.completed");
    }

    [Fact]
    public async Task A_unit_that_rolls_back_leaves_no_event_no_commit_mark_and_no_row()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var key = Held(Key("k9"), await ledger.ClaimAsync(Key("k9"), _now, Ct));

        await Should.ThrowAsync<InvalidOperationException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "lost" }, ct);
            await ((IAlvoCommandUnit)unit).PublishFrameworkAsync(Completion(), ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, _now, ct);
            throw new InvalidOperationException("the handler failed after its writes");
        }, cancellationToken: Ct));

        (await ledger.ClaimAsync(Key("k9"), _now.AddSeconds(1), Ct)).State.ShouldBe(CommandClaimState.InFlight, "the claim is untouched: only the commit mark rolled back");
        (await PendingTypesAsync(services)).ShouldNotContain("alvo.command.quote_stay.completed");
        (await services.GetRequiredService<IAlvoData>().QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_whose_lease_was_taken_over_cannot_commit_and_its_writes_roll_back()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var stale = Held(Key("k10"), await ledger.ClaimAsync(Key("k10"), _now, Ct));
        var current = Held(Key("k10"), await ledger.ClaimAsync(Key("k10"), _now + _pastTheLease, Ct));

        await Should.ThrowAsync<CommandClaimLostException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "stale run" }, ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(stale, _now + _pastTheLease, ct);
        }, cancellationToken: Ct));

        (await services.GetRequiredService<IAlvoData>().QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct)).Items.ShouldBeEmpty("the stale run's write rolled back");
        await services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, (unit, ct) =>
            ((IAlvoCommandUnit)unit).CommitCommandAsync(current, _now + _pastTheLease, ct), cancellationToken: Ct);
        (await ledger.ClaimAsync(Key("k10"), _now.AddHours(1), Ct)).State.ShouldBe(CommandClaimState.Unanswered, "the run holding the claim commits");
    }

    /// <summary>
    /// A handler that swallows the refusal and returns normally still cannot commit: the lost claim poisoned the unit
    /// (Ruling 36).
    /// </summary>
    [Fact]
    public async Task A_handler_that_catches_its_lost_claim_still_cannot_commit_its_writes_or_its_event()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var stale = Held(Key("k12"), await ledger.ClaimAsync(Key("k12"), _now, Ct));
        await ledger.ClaimAsync(Key("k12"), _now + _pastTheLease, Ct);

        await Should.ThrowAsync<CommandClaimLostException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "swallowed" }, ct);
            await ((IAlvoCommandUnit)unit).PublishFrameworkAsync(Completion(), ct);
            try
            {
                await ((IAlvoCommandUnit)unit).CommitCommandAsync(stale, _now + _pastTheLease, ct);
            }
            catch (CommandClaimLostException)
            {
            }
        }, cancellationToken: Ct));

        (await services.GetRequiredService<IAlvoData>().QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct)).Items.ShouldBeEmpty();
        (await PendingTypesAsync(services)).ShouldNotContain("alvo.command.quote_stay.completed");
    }

    [Fact]
    public async Task Committing_a_key_that_was_never_claimed_is_refused_as_a_programming_error()
    {
        var services = await StartAsync(Schema(), Descriptor());

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, (unit, ct) =>
            ((IAlvoCommandUnit)unit).CommitCommandAsync(Key("k13"), _now, ct), cancellationToken: Ct));

        refusal.ShouldNotBeOfType<CommandClaimLostException>();
        refusal.Message.ShouldContain("never claimed");
    }

    [Fact]
    public async Task Completing_with_a_key_that_was_never_claimed_is_refused_as_a_programming_error()
    {
        var ledger = await LedgerAsync();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            ledger.CompleteAsync(Completion(), Key("k14"), new CommandAnswer(200, null), _now, Ct));

        refusal.ShouldNotBeOfType<CommandClaimLostException>();
        refusal.Message.ShouldContain("never claimed");
    }

    [Fact]
    public async Task Command_keys_claimed_more_than_a_day_ago_are_swept_and_their_key_runs_again()
    {
        var ledger = await LedgerAsync();
        var old = await ledger.ClaimAsync(Key("old"), _now.AddHours(-25), Ct);
        await ledger.CompleteAsync(Completion(), Held(Key("old"), old), new CommandAnswer(200, null), _now.AddHours(-25), Ct);
        var young = await ledger.ClaimAsync(Key("young"), _now.AddHours(-1), Ct);
        await ledger.CompleteAsync(Completion(), Held(Key("young"), young), new CommandAnswer(200, null), _now.AddHours(-1), Ct);

        (await ledger.SweepAsync(_now - CommandLedgerLimits.Retention, Ct)).ShouldBe(1);

        (await ledger.ClaimAsync(Key("old"), _now, Ct)).State.ShouldBe(CommandClaimState.Claimed, "an expired key is a new request");
        (await ledger.ClaimAsync(Key("young"), _now, Ct)).State.ShouldBe(CommandClaimState.Answered);
    }

    [Fact]
    public async Task A_webhook_id_is_claimed_once_completed_and_then_found_completed()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();

        var claim = await ledger.ClaimAsync("/hooks/pay", "msg_1", _now, Ct);
        claim.State.ShouldBe(WebhookClaim.Claimed);
        (await ledger.ClaimAsync("/hooks/pay", "msg_1", _now, Ct)).State.ShouldBe(WebhookClaim.InFlight);
        (await ledger.CompleteAsync("/hooks/pay", "msg_1", claim.Token!, _now, Ct)).ShouldBeTrue();
        (await ledger.ClaimAsync("/hooks/pay", "msg_1", _now.AddDays(1), Ct)).State.ShouldBe(WebhookClaim.Completed);
        (await ledger.ClaimAsync("/hooks/other", "msg_1", _now, Ct)).State.ShouldBe(WebhookClaim.Claimed, "dedup is per receiver");
    }

    [Fact]
    public async Task A_stale_webhook_run_can_neither_complete_nor_release_a_live_claim()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();
        var stale = await ledger.ClaimAsync("/hooks/pay", "msg_2", _now, Ct);
        var live = await ledger.ClaimAsync("/hooks/pay", "msg_2", _now + InboundWebhookLimits.Lease + TimeSpan.FromSeconds(1), Ct);

        (await ledger.CompleteAsync("/hooks/pay", "msg_2", stale.Token!, _now, Ct)).ShouldBeFalse();
        await ledger.ReleaseAsync("/hooks/pay", "msg_2", stale.Token!, Ct);

        live.State.ShouldBe(WebhookClaim.Claimed);
        (await ledger.ClaimAsync("/hooks/pay", "msg_2", _now + InboundWebhookLimits.Lease + TimeSpan.FromSeconds(2), Ct)).State.ShouldBe(WebhookClaim.InFlight);
    }

    [Fact]
    public async Task The_sweep_removes_ids_received_before_its_cutoff_and_keeps_later_ones()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();
        var old = await ledger.ClaimAsync("/hooks/pay", "old", _now.AddDays(-8), Ct);
        await ledger.CompleteAsync("/hooks/pay", "old", old.Token!, _now.AddDays(-8), Ct);
        var young = await ledger.ClaimAsync("/hooks/pay", "young", _now.AddDays(-6), Ct);
        await ledger.CompleteAsync("/hooks/pay", "young", young.Token!, _now.AddDays(-6), Ct);

        (await ledger.SweepAsync(_now - InboundWebhookLimits.Retention, Ct)).ShouldBe(1);

        (await ledger.ClaimAsync("/hooks/pay", "old", _now, Ct)).State.ShouldBe(WebhookClaim.Claimed, "a swept id is forgotten");
        (await ledger.ClaimAsync("/hooks/pay", "young", _now, Ct)).State.ShouldBe(WebhookClaim.Completed);
    }

    private async Task<ICommandLedger> LedgerAsync() =>
        (await StartAsync(Schema(), Descriptor())).GetRequiredService<ICommandLedger>();

    private static CommandKey Key(string key) => new("command:quote_stay/global/" + _caller.User.Value + "/-", key, "fp");

    private static CommandKey Held(CommandKey key, CommandClaim claim) => key with { Token = claim.Token };

    private static AlvoEvent Completion() => AlvoFrameworkEvent.Create(
        "alvo.command.quote_stay.completed", "commands/quote_stay", new Dictionary<string, object?> { ["command"] = "quote_stay" }, _caller, _now);

    private static async Task<IReadOnlyList<string>> PendingTypesAsync(IServiceProvider services)
    {
        var entries = await services.GetRequiredService<IOutboxStore>().ClaimAsync("ledger-facts", 100, 10, TimeSpan.FromMinutes(1), Ct);
        return [.. entries.Select(entry => entry.Type)];
    }

    protected static SchemaModel Schema() => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "text", Type = SchemaField.String, Required = true, MaxLength = 80 },
            ],
        },
    ]);

    protected static AlvoDescriptor Descriptor() => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "ledger-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["text"] = new() { Type = DescField.String, Required = true, MaxLength = 80 },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };
}
