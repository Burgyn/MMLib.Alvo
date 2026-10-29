using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using NSubstitute;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// One command run (spec 2026-10-14 §4.9, R-M7): at most one unit; the completion event and the key's record in that unit
/// when one commits, else together after a 2xx; nothing after a refusal or a rolled-back unit; a committed run is never re-run.
/// </summary>
public sealed class CommandRunnerTests
{
    private static readonly AlvoContext _staff = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static readonly CommandRegistration _quote =
        new("quote_stay", 1, CommandAccess.Of("true"), null, "/commands/quote", null, null);

    private readonly FakeCommandLedger _ledger = new();

    private FakeTransactions _transactions = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_claim_hands_the_run_its_fencing_token()
    {
        var runner = Runner(out _);

        var claimed = await runner.ClaimAsync(Invocation("k0"));

        claimed.Early.ShouldBeNull();
        claimed.Invocation.Key!.Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_run_without_a_unit_answering_2xx_completes_with_its_event_and_its_answer()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k1"))).Invocation);

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.Ok(new { total = 3 })));

        _ledger.Calls.ShouldBe(["claim", "complete"]);
        _ledger.Completions.ShouldHaveSingleItem().Type.ShouldBe("alvo.command.quote_stay.completed");
        (await runner.ClaimAsync(Invocation("k1"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task A_run_without_a_unit_answering_4xx_releases_its_key_and_emits_nothing()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k2"))).Invocation);

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.BadRequest()));

        _ledger.Calls.ShouldBe(["claim", "release"]);
        _ledger.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_unit_carries_the_completion_and_the_commit_mark_and_the_answer_follows()
    {
        var runner = Runner(out var unit);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k3"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            return TypedResults.Created("/notes/1", new { id = 1 });
        });

        unit.Published.ShouldHaveSingleItem().Type.ShouldBe("alvo.command.quote_stay.completed");
        _ledger.Calls.ShouldBe(["claim", "answer"], "the event was in the unit; only the answer is written after it");
        (await runner.ClaimAsync(Invocation("k3"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(201);
    }

    [Fact]
    public async Task A_committed_unit_followed_by_a_non_success_answer_keeps_its_event_and_records_the_answer()
    {
        var runner = Runner(out var unit);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k4"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            return TypedResults.Conflict();
        });

        unit.Published.ShouldHaveSingleItem();
        _ledger.Calls.ShouldNotContain("release", "a committed run is never given back to be run again");
        (await runner.ClaimAsync(Invocation("k4"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task A_handler_that_catches_its_rolled_back_unit_and_answers_2xx_emits_no_completion()
    {
        var runner = Runner(out _);
        _transactions.RollBack = true;
        var run = runner.Start((await runner.ClaimAsync(Invocation("k8"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            try
            {
                await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            }
            catch (InvalidOperationException)
            {
            }

            return TypedResults.Ok();
        });

        _ledger.Completions.ShouldBeEmpty("its writes rolled back, so nothing completed");
        _ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_handler_throwing_after_its_unit_committed_records_a_500_and_is_not_released()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k5"))).Invocation);
        var context = new AlvoCommandContext(run);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => Task.CompletedTask, cancellationToken: Ct);
            throw new InvalidOperationException("after the commit");
        }));

        _ledger.Calls.ShouldBe(["claim", "answer"]);
        (await runner.ClaimAsync(Invocation("k5"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task A_handler_throwing_before_any_unit_releases_its_key()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k6"))).Invocation);

        await Should.ThrowAsync<TimeoutException>(() => runner.FinishAsync(run, () => throw new TimeoutException("the payment provider")));

        _ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_second_unit_throws_and_the_first_one_stands()
    {
        var runner = Runner(out var unit);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));
        var context = new AlvoCommandContext(run);

        await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct);
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));

        refusal.Message.ShouldContain("at most one");
        unit.Published.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_context_whose_run_answered_cannot_open_a_unit()
    {
        var runner = Runner(out _);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));
        await runner.FinishAsync(run, () => Task.FromResult<object?>(null));

        await Should.ThrowAsync<InvalidOperationException>(() => new AlvoCommandContext(run).TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_key_still_in_flight_is_a_409_and_a_key_spent_on_another_request_is_the_idempotency_conflict()
    {
        var runner = Runner(out _);
        await runner.ClaimAsync(Invocation("k7"));

        var inFlight = await runner.ClaimAsync(Invocation("k7"));
        var mismatch = Should.ThrowAsync<AlvoIdempotencyConflictException>(() => runner.ClaimAsync(new CommandInvocation(_quote, _staff, null, Key("k7") with { Fingerprint = "other" })));

        inFlight.Early.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await mismatch;
    }

    [Fact]
    public async Task A_union_result_answering_4xx_releases_its_key_and_emits_nothing()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("u1"))).Invocation);
        Results<Ok<Quote>, BadRequest> refused = TypedResults.BadRequest();

        await runner.FinishAsync(run, () => Task.FromResult<object?>(refused));

        _ledger.Calls.ShouldBe(["claim", "release"]);
        _ledger.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_union_result_answering_200_replays_the_arm_it_answered_with_its_body()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("u2"))).Invocation);
        Results<Ok<Quote>, NotFound> quoted = TypedResults.Ok(new Quote(3));

        await runner.FinishAsync(run, () => Task.FromResult<object?>(quoted));

        var replay = await ReplayedAsync(await runner.ClaimAsync(Invocation("u2")));
        replay.ShouldBe((200, "application/json; charset=utf-8", "{\"total\":3}"));
    }

    [Fact]
    public async Task A_problem_answered_after_the_unit_committed_replays_as_a_problem_document()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("p1"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct);
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already booked");
        });

        var replay = await ReplayedAsync(await runner.ClaimAsync(Invocation("p1")));
        replay.Status.ShouldBe(409);
        replay.ContentType.ShouldBe("application/problem+json; charset=utf-8");
        replay.Body.ShouldContain("\"title\":\"Already booked\"");
    }

    [Fact]
    public async Task A_keyed_run_whose_answer_cannot_be_serialised_completes_without_an_answer()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("s1"))).Invocation);

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.Ok(Cycle.Of())));

        _ledger.Calls.ShouldBe(["claim", "complete"]);
        (await runner.ClaimAsync(Invocation("s1"))).Early.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode
            .ShouldBe(StatusCodes.Status409Conflict, "it ran and committed; a retry must not run it again");
    }

    [Fact]
    public async Task A_keyless_run_records_no_answer_so_an_unserialisable_value_is_never_serialised()
    {
        var runner = Runner(out _);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.Ok(Cycle.Of())));

        _ledger.Completions.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_keyed_invocation_the_claim_did_not_grant_cannot_start()
    {
        var runner = Runner(out _);

        var refusal = Should.Throw<InvalidOperationException>(() => runner.Start(Invocation("c1")));

        refusal.Message.ShouldContain("unclaimed");
    }

    [Fact]
    public async Task A_bookkeeping_failure_after_the_handler_threw_rethrows_the_handlers_own_exception()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("b1"))).Invocation);
        _ledger.ReleaseFailure = new InvalidOperationException("the ledger is down");

        await Should.ThrowAsync<TimeoutException>(() => runner.FinishAsync(run, () => throw new TimeoutException("the payment provider")));

        _ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_host_without_transaction_units_refuses_the_unit_without_counting_it_as_opened()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var runner = new CommandRunner(
            _ledger, null, Substitute.For<IAlvoSessions>(), new CommandCompletion(time), time, JsonSerializerOptions.Web, NullLogger<CommandRunner>.Instance);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => new AlvoCommandContext(run).TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));

        refusal.Message.ShouldContain("IAlvoTransactions");
        run.OpenedWithoutCommit.ShouldBeFalse();
    }

    [Fact]
    public void A_plain_value_is_a_200_answer()
    {
        CommandAnswers.StatusOf(null).ShouldBe(200);
        CommandAnswers.StatusOf(new { total = 3 }).ShouldBe(200);
    }

    [Fact]
    public void A_plain_string_is_not_replayable_because_it_was_written_as_text() =>
        CommandAnswers.Of("done", JsonSerializerOptions.Web).ShouldBeNull();

    [Fact]
    public void A_result_with_no_status_is_not_replayable() =>
        CommandAnswers.Of(TypedResults.Redirect("/elsewhere"), JsonSerializerOptions.Web).ShouldBeNull();

    [Fact]
    public void An_answer_over_the_cap_is_not_replayable() =>
        CommandAnswers.Of(TypedResults.Ok(new string('x', 70_000)), JsonSerializerOptions.Web).ShouldBeNull();

    private CommandRunner Runner(out FakeCommandUnit unit)
    {
        unit = new FakeCommandUnit(_ledger, _staff);
        _transactions = new FakeTransactions(unit);
        var sessions = Substitute.For<IAlvoSessions>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

        return new CommandRunner(_ledger, _transactions, sessions, new CommandCompletion(time), time, JsonSerializerOptions.Web, NullLogger<CommandRunner>.Instance);
    }

    private static async Task<(int Status, string? ContentType, string Body)> ReplayedAsync(CommandClaimed claimed)
    {
        var http = new DefaultHttpContext();
        using var body = new MemoryStream();
        http.Response.Body = body;

        await claimed.Early.ShouldBeOfType<ReplayedAnswer>().ExecuteAsync(http);

        return (http.Response.StatusCode, http.Response.ContentType, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static CommandInvocation Invocation(string key) => new(_quote, _staff, null, Key(key));

    private static CommandKey Key(string key) => new($"command:quote_stay/global/{_staff.User.Value}/-", key, "fp");
}

/// <summary>A command's answer value.</summary>
/// <param name="Total">The quoted total.</param>
public sealed record Quote(int Total);

/// <summary>A value that refers to itself, which JSON cannot write.</summary>
public sealed class Cycle
{
    /// <summary>Gets itself.</summary>
    public Cycle? Self { get; private set; }

    internal static Cycle Of()
    {
        var loop = new Cycle();
        loop.Self = loop;
        return loop;
    }
}
