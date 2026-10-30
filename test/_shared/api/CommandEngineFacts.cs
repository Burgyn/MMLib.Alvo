using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Net;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Api.Tests.CommandWorld;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Commands over HTTP (spec 2026-10-14 §4.9, §7 HX-08 acceptance (1), (2), (3), (10)) — the same facts on SQLite and on
/// PostgreSQL. Each fact starts its own world.
/// </summary>
public abstract class CommandEngineFacts
{
    private readonly CommandProbe _probe = new();

    protected abstract AlvoApiEngine Engine { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_access_rule_that_refuses_the_caller_answers_403_before_the_handler_runs()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Guest);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        _probe.Runs.ShouldBe(0, "access is judged before the handler");
    }

    [Fact]
    public async Task An_access_rule_reading_the_row_refuses_a_row_in_the_wrong_state()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User, status: "confirmed");

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task An_entity_command_on_a_row_the_caller_cannot_read_answers_404_before_the_handler_runs()
    {
        await using var world = await StartAsync(Engine, _probe);
        var someoneElses = await BookAsync(world, Guid.NewGuid());

        using var unreadable = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{someoneElses}/confirm", Guest);
        using var absent = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{Guid.NewGuid()}/confirm", Staff);

        unreadable.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a row the caller cannot read is 404 — before, and instead of, the access rule's 403");
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await unreadable.ReadProblemDetailAsync()).ShouldBe(await absent.ReadProblemDetailAsync(), "the two must be indistinguishable");
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_key_whose_scopes_exclude_writing_the_entity_is_403_out_of_scope()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", ReadOnlyStaff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task No_credential_is_judged_as_anonymous_and_an_unknown_key_is_401()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: new JsonObject { ["nights"] = 2 });
        using var ghost = await world.SendAsync(HttpMethod.Post, "/commands/quote", new TestApiKey("ghost-key", ["staff"], ["*:write"]), body: new JsonObject { ["nights"] = 2 });

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the anonymous caller holds neither role the rule asks for");
        ghost.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_completed_command_s_event_reaches_an_event_handler()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Completions.ShouldBe(["confirm_booking"]);
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_command_with_no_unit_completes_after_its_2xx()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", Guest, body: new JsonObject { ["nights"] = 3 });
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonObjectAsync())["total"]!.GetValue<decimal>().ShouldBe(240m);
        _probe.Completions.ShouldBe(["quote_stay"]);
    }

    [Fact]
    public async Task A_replayed_idempotency_key_answers_the_first_result_without_running_the_handler_again()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var first = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("confirm-1"));
        using var replay = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("confirm-1"));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, "the replay is answered before the row's new state is judged (Ruling 7)");
        JsonNode.DeepEquals(await first.ReadJsonObjectAsync(), await replay.ReadJsonObjectAsync()).ShouldBeTrue();
        _probe.Runs.ShouldBe(1);
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_key_spent_by_one_caller_is_not_replayed_to_another()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var mine = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("shared"));
        using var theirs = await world.SendAsync(HttpMethod.Post, "/commands/quote", OtherStaff, body: new JsonObject { ["nights"] = 1 }, headers: Key("shared"));

        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        theirs.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Runs.ShouldBe(2, "a key is scoped to the caller who sent it");
    }

    [Fact]
    public async Task A_key_spent_by_one_api_key_is_not_replayed_to_another_key_of_the_same_user()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var staff = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("shared-by-a-user"));
        using var lowerKey = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", StaffAsGuest, headers: Key("shared-by-a-user"));

        staff.StatusCode.ShouldBe(HttpStatusCode.OK);
        lowerKey.StatusCode.ShouldNotBe(HttpStatusCode.OK, "the replay is scoped to the credential: the guest-role key is judged on its own");
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_that_writes_but_cannot_read_the_bound_entity_is_403_out_of_scope()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", WriteOnlyStaff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_negated_tenant_rule_refuses_a_tenantless_caller()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/commands/guarded", body: new JsonObject { ["nights"] = 1 });
        using var tenantless = await world.SendAsync(HttpMethod.Post, "/commands/guarded", Staff, body: new JsonObject { ["nights"] = 1 });

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "no tenant: the rule's negation must not admit by the absent operand");
        tenantless.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_key_reused_with_a_different_body_is_an_idempotency_conflict()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var first = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 2 }, headers: Key("q"));
        using var second = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 3 }, headers: Key("q"));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task An_anonymous_caller_sending_a_key_is_refused_422()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: new JsonObject { ["nights"] = 2 }, headers: Key("anon"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task Two_concurrent_requests_with_one_key_run_the_handler_once()
    {
        await using var world = await StartAsync(Engine, _probe);

        var first = world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("hold-1"));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var second = await world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("hold-1"));
        _probe.Release.SetResult();
        using var firstAnswer = await first;

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the key is still being processed");
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        firstAnswer.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_that_outlived_its_lease_is_answered_409_and_the_run_that_took_its_key_over_stands()
    {
        await using var world = await StartAsync(Engine, _probe);

        var stale = world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("lease-1"));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _probe.Clock.Advance(TimeSpan.FromMinutes(16));
        using var takeover = await world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("lease-1"));
        _probe.Release.SetResult();
        using var staleAnswer = await stale;
        using var retry = await world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("lease-1"));

        takeover.StatusCode.ShouldBe(HttpStatusCode.OK, "the lease ran out, so a retry takes the key over");
        staleAnswer.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the first run lost its claim, so its completion is refused");
        (await staleAnswer.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, "the key answers with the run that took it over");
        _probe.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_handler_calling_the_injected_data_port_or_its_context_s_data_inside_its_unit_is_refused()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/misuse/data-port", Staff).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Refusal.ShouldBeOfType<AlvoUnitInProgressException>();
        _probe.ContextDataRefusal.ShouldBeOfType<AlvoUnitInProgressException>("the context's own data session is refused inside its unit too");
        (await NotesAsync(world)).ShouldBe(0, "no side write committed");
    }

    [Fact]
    public async Task An_external_call_before_the_unit_runs_with_no_transaction_open()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the external call's own IAlvoData read would have been refused inside a unit");
        _probe.UnitOpenDuringExternalCall.ShouldBe(false);
    }

    [Fact]
    public async Task A_second_TransactAsync_throws_and_the_first_unit_stands()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/misuse/two-units", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Refusal.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("at most one");
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task The_completion_event_and_the_idempotency_record_commit_in_the_handler_s_unit()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var failed = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-in-unit", Staff, headers: Key("in-unit"));
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);
        using var retried = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-in-unit", Staff, headers: Key("in-unit"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Runs.ShouldBe(2, "the unit rolled back its key's record with its writes, so the retry runs again");
        _probe.Completions.ShouldBeEmpty("the completion rolled back with the unit");
        (await NotesAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task A_handler_that_throws_after_its_unit_committed_is_not_run_again()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var failed = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-after-unit", Staff, headers: Key("after-unit"));
        using var retried = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-after-unit", Staff, headers: Key("after-unit"));
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, "the recorded answer is replayed");
        _probe.Runs.ShouldBe(1);
        _probe.Completions.ShouldHaveSingleItem("its writes and its completion committed").ShouldBe("misuse.fail_after_unit");
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_command_s_answer_is_never_stored_by_a_cache()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 1 });

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
