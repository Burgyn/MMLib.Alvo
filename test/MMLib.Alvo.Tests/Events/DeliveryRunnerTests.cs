using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

namespace MMLib.Alvo.Tests.Events;

/// <summary>One delivery, end to end over the in-memory store: who it runs as, how deep, and how each outcome is recorded.</summary>
public sealed class DeliveryRunnerTests : IDisposable
{
    private const string Handler = "on-created";
    private const string Claimant = "worker-1";
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    private static readonly Guid _tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeDeliveryStore _store;
    private readonly IAlvoData _data = Substitute.For<IAlvoData>();

    public DeliveryRunnerTests()
    {
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _store = new FakeDeliveryStore(_clock);
    }

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handler_that_returns_completes_its_delivery_and_records_the_attempt()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var delivery = await ClaimedAsync();

        (await runner.RunAsync(delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Succeeded);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("succeeded");
        _store.Attempts.ShouldBe([(delivery.Id, 1, "succeeded")]);
    }

    [Fact]
    public async Task The_handler_runs_one_hop_deeper_than_its_event_and_a_fresh_system_context_inherits_it()
    {
        AlvoProvenance? ambient = null;
        AlvoProvenance? effective = null;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
        {
            ambient = AlvoFlow.Provenance;
            effective = AlvoEventProvenance.Effective(AlvoContext.System(null));
            return Task.CompletedTask;
        });
        var @event = await ClaimedEventAsync(chainDepth: 3);

        await runner.RunAsync(@event.Delivery, handler, Claimant, Ct);

        ambient.ShouldNotBeNull().Depth.ShouldBe(4);
        ambient.CausationId.ShouldBe(@event.Event.Id.ToString());
        ambient.CorrelationId.ShouldBe(@event.Event.CorrelationId);
        ambient.TraceParent.ShouldBeNull("writes in a handler are traced under the delivery span, not the original request");
        effective.ShouldNotBeNull().Depth.ShouldBe(4);
        AlvoFlow.Provenance.ShouldBeNull("the flow ends with the delivery");
    }

    [Fact]
    public async Task A_system_handler_acts_as_the_system_in_the_event_s_tenant()
    {
        AlvoContext? caller = null;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
        {
            caller = context.Caller;
            return Task.CompletedTask;
        });

        await runner.RunAsync((await ClaimedEventAsync()).Delivery, handler, Claimant, Ct);

        caller.ShouldNotBeNull().User.ShouldBe(AlvoContext.System(null).User);
        caller.Tenant.ShouldBe(new TenantId(_tenant));
        caller.Roles.ShouldBe([Role.Admin]);
    }

    [Fact]
    public async Task An_event_actor_handler_acts_as_the_caller_who_made_the_change()
    {
        AlvoContext? caller = null;
        var user = Guid.NewGuid();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            {
                caller = context.Caller;
                return Task.CompletedTask;
            },
            options => options.RunAs = AlvoRunAs.EventActor);

        await runner.RunAsync((await ClaimedEventAsync(authId: user.ToString())).Delivery, handler, Claimant, Ct);

        caller.ShouldNotBeNull().User.Value.ShouldBe(user);
        caller.Tenant.ShouldBe(new TenantId(_tenant));
    }

    [Fact]
    public async Task The_handler_s_data_is_a_session_bound_to_its_caller_never_the_unbound_data_port()
    {
        IAlvoSession? data = null;
        var (runner, handler) = Arrange(async (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
        {
            data = context.Data;
            await context.Data.CreateAsync("notes", new Dictionary<string, object?>(), CancellationToken.None);
        });

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);

        data.ShouldNotBeNull().ShouldNotBeAssignableTo<IAlvoData>();
        await _data.Received(1).CreateAsync(
            "notes",
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Is<AlvoContext>(context => context.User == AlvoContext.System(null).User && context.Tenant == new TenantId(_tenant)),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_delivery_runs_in_its_own_service_scope_disposed_when_it_ends()
    {
        var seen = new List<ScopedProbe>();
        using var services = new ServiceCollection().AddScoped<ScopedProbe>().BuildServiceProvider();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _, ScopedProbe probe) =>
            {
                seen.Add(probe);
                return Task.CompletedTask;
            },
            services: services);

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);
        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);

        seen.Count.ShouldBe(2);
        seen[0].ShouldNotBeSameAs(seen[1], "one scope per delivery, never the root provider");
        seen.ShouldAllBe(probe => probe.Disposed);
    }

    [Fact]
    public async Task A_failure_waits_for_its_retry_and_records_the_exception_s_type_never_its_message()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException("card 4111-1111")));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Failed);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("retrying");
        row.LastErrorType.ShouldBe(typeof(InvalidOperationException).FullName);
        row.NotBefore.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(5));
        _logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("4111"));
    }

    [Fact]
    public async Task A_failure_is_logged_by_its_exception_s_type_and_carries_no_exception_object()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException("card 4111-1111")));

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);

        var failed = _logs.Entries.Where(entry => entry.EventName == "DeliveryFailed").ShouldHaveSingleItem();
        failed.Message.ShouldContain(typeof(InvalidOperationException).FullName!);
        failed.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task The_last_attempt_s_failure_is_dead()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()), options => options.Retry = AlvoRetryPolicy.None);

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("dead");
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryDead" && entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_write_refused_for_depth_is_dead_at_once_and_never_retried()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(AlvoChainDepthExceededException.At(9, 8)));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_typed_message_that_cannot_be_built_is_dead_at_once_and_names_its_field_never_a_value()
    {
        var (runner, handler) = Arrange(
            (AlvoCreated<TypedTicket> _) => Task.CompletedTask,
            register: (alvo, handle) => alvo.Entity<TypedTicket>(tickets => tickets.OnCreated(Handler, handle)));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("record-unreadable");
        var unreadable = _logs.Entries.Where(entry => entry.EventName == "DeliveryRecordUnreadable").ShouldHaveSingleItem();
        unreadable.Message.ShouldContain("'id'");
        unreadable.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task A_write_the_data_port_refuses_for_depth_through_the_session_is_dead_at_once()
    {
        _data.CreateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<AlvoContext>(), Arg.Any<AlvoIdempotency?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(AlvoChainDepthExceededException.At(9, 8));
        var (runner, handler) = Arrange(async (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
        {
            await context.Data.CreateAsync("notes", new Dictionary<string, object?>(), CancellationToken.None);
        });

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_handler_that_outlives_its_timeout_is_cancelled_and_counted_as_failed()
    {
        var (runner, handler) = Arrange(
            async (AlvoCreated<AlvoRecord> _, CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct),
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System);

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Failed);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_timed_out_handler_s_token_is_cancelled_before_the_attempt_is_answered()
    {
        var token = CancellationToken.None;
        var never = new TaskCompletionSource();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _, CancellationToken ct) =>
            {
                token = ct;
                return never.Task;
            },
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);
        never.SetResult();

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_is_answered_at_its_timeout_and_only_once()
    {
        var never = new TaskCompletionSource();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _) => never.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));

        var outcome = await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        never.SetResult();

        outcome.ShouldBe(DeliveryRunOutcome.Failed, "the runner answers at the timeout whatever the handler does");
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("timeout");
        _store.Attempts.Count.ShouldBe(1, "the attempt is answered once; the handler finishing late records nothing");
    }

    [Fact]
    public async Task A_handler_that_blocks_its_thread_is_still_answered_at_its_timeout()
    {
        using var release = new ManualResetEventSlim();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _) =>
            {
                release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
                return Task.CompletedTask;
            },
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));

        var outcome = await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.Set();

        outcome.ShouldBe(DeliveryRunOutcome.Failed);
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_depth_refusal_wrapped_in_another_exception_is_still_dead_at_once()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
            Task.FromException(new InvalidOperationException("wrapped", AlvoChainDepthExceededException.At(9, 8))));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_depth_refusal_among_an_aggregate_s_failures_is_still_dead_at_once()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
            Task.FromException(new AggregateException(new InvalidOperationException(), AlvoChainDepthExceededException.At(9, 8))));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task An_envelope_with_an_unreadable_tenant_is_dead_and_never_runs_as_a_tenantless_system()
    {
        var ran = false;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; });

        var claimed = await ClaimedEventAsync(tenantText: "not-a-tenant");
        (await runner.RunAsync(claimed.Delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-unreadable");
    }

    [Fact]
    public async Task An_event_actor_envelope_with_an_unreadable_credential_is_dead_and_never_runs_as_anonymous()
    {
        var ran = false;
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; },
            options => options.RunAs = AlvoRunAs.EventActor);

        var claimed = await ClaimedEventAsync(authId: "not-a-user");
        (await runner.RunAsync(claimed.Delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-unreadable");
    }

    [Fact]
    public async Task A_stored_envelope_that_is_not_an_alvo_event_is_dead_and_never_runs()
    {
        var ran = false;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; });

        (await runner.RunAsync(await ClaimedAsync() with { Payload = "{\"specversion\":\"0.3\"}" }, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-unreadable");
    }

    [Fact]
    public async Task A_shutdown_mid_handler_records_nothing_and_leaves_the_claim()
    {
        using var stopping = new CancellationTokenSource();
        var (runner, handler) = Arrange(async (AlvoCreated<AlvoRecord> _, CancellationToken ct) =>
        {
            await stopping.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, stopping.Token)).ShouldBe(DeliveryRunOutcome.Shutdown);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("running");
        _store.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_finisher_whose_lease_was_reclaimed_changes_nothing_and_says_so()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var late = await ClaimedAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _store.ClaimAsync(Claim(), Ct);

        (await runner.RunAsync(late, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.LeaseLost);

        _store.Rows.ShouldHaveSingleItem().Attempts.ShouldBe(2);
        _store.Attempts.ShouldBeEmpty();
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryLeaseLost");
    }

    [Fact]
    public async Task A_delivery_reclaimed_past_its_last_attempt_is_dead_without_running()
    {
        var ran = false;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; }, options => options.Retry = AlvoRetryPolicy.None);
        await ClaimedAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = (await _store.ClaimAsync(Claim(), Ct)).Single();

        (await runner.RunAsync(reclaimed, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("lease-expired");
    }

    [Fact]
    public async Task A_delivery_whose_envelope_is_gone_is_dead()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);

        (await runner.RunAsync(await ClaimedAsync() with { Payload = null }, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-missing");
    }

    [Fact]
    public async Task The_delivery_span_links_the_envelope_trace_and_keeps_the_ambient_parent()
    {
        using var spans = new Spans();
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var claimed = await ClaimedEventAsync(traceParent: TraceParent);

        var root = await spans.WithinAsync(() => runner.RunAsync(claimed.Delivery, handler, Claimant, Ct));

        var span = spans.Recorded.ShouldHaveSingleItem();
        span.TraceId.ShouldBe(root, "the parent stays the drain or loop that ran it, which is what the test host's recorder scopes by");
        span.Links.ShouldHaveSingleItem().Context.TraceId.ToString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        span.GetTagItem("alvo.subscriber").ShouldBe(Handler);
        span.GetTagItem("alvo.delivery.attempt").ShouldBe(1);
        span.GetTagItem("alvo.outcome").ShouldBe("succeeded");
    }

    [Fact]
    public async Task What_the_handler_does_is_traced_under_its_delivery_span()
    {
        using var spans = new Spans();
        ActivitySpanId? current = null;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
        {
            current = Activity.Current?.SpanId;
            return Task.CompletedTask;
        });
        var claimed = await ClaimedEventAsync(traceParent: TraceParent);

        await spans.WithinAsync(() => runner.RunAsync(claimed.Delivery, handler, Claimant, Ct));

        current.ShouldBe(spans.Recorded.ShouldHaveSingleItem().SpanId);
    }

    [Fact]
    public async Task A_handler_s_own_timeout_exception_is_recorded_by_its_type_not_as_the_runner_s_timeout()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new TimeoutException()));

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe(typeof(TimeoutException).FullName);
    }

    [Fact]
    public void A_handler_that_ended_by_the_time_its_deadline_is_handled_is_answered_by_its_own_result()
    {
        DeliveryRunner.Ended(Task.CompletedTask).Succeeded.ShouldBeTrue("a handler that finished at the deadline succeeded");
        DeliveryRunner.Ended(Task.FromException(new InvalidOperationException())).ErrorType.ShouldBe(typeof(InvalidOperationException).FullName);
        DeliveryRunner.Ended(Task.FromException(AlvoChainDepthExceededException.At(9, 8))).ShouldBe(new DeliveryRunner.AttemptVerdict("chain-depth-exceeded", false));
        DeliveryRunner.Ended(Task.FromCanceled(new CancellationToken(canceled: true))).ErrorType.ShouldBe(typeof(TaskCanceledException).FullName);
    }

    [Fact]
    public async Task The_store_answers_for_each_attempt_outside_the_handler_s_flow()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()));
        var (_, succeeding) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);

        await runner.RunAsync((await ClaimedEventAsync(chainDepth: 3)).Delivery, handler, Claimant, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await runner.RunAsync((await ClaimedEventAsync(chainDepth: 3)).Delivery, succeeding, Claimant, Ct);

        _store.AnswerFlows.Count.ShouldBe(2);
        _store.AnswerFlows.ShouldAllBe(flow => flow == null, "recording an attempt is the runner's work, not the handler's next hop");
    }

    [Fact]
    public async Task An_abandoned_handler_s_later_failure_is_logged_by_its_type_only()
    {
        var release = new TaskCompletionSource();
        var (runner, handler) = Arrange(
            async (AlvoCreated<AlvoRecord> _) =>
            {
                await release.Task;
                throw new InvalidOperationException("card 4111-1111");
            },
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));

        await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct);
        release.SetResult();
        var line = await LoggedAsync("AbandonedHandlerFailed");

        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain(typeof(InvalidOperationException).FullName!);
        line.Message.ShouldNotContain("4111");
        _store.Attempts.Count.ShouldBe(1, "the abandoned handler's failure records nothing");
    }

    [Fact]
    public async Task A_late_finisher_s_span_says_its_lease_was_lost_not_that_it_succeeded()
    {
        using var spans = new Spans();
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var late = (await ClaimedEventAsync(traceParent: TraceParent)).Delivery;
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _store.ClaimAsync(Claim(), Ct);

        await spans.WithinAsync(() => runner.RunAsync(late, handler, Claimant, Ct));

        spans.Recorded.ShouldHaveSingleItem().GetTagItem("alvo.outcome").ShouldBe("lease-lost");
    }

    [Fact]
    public async Task A_storage_refusal_of_the_answer_is_written_again_inside_the_lease()
    {
        var refusing = new AnswerRefusingStore(_store, refusals: 2, () => new BusyStorageException());
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask, port: refusing);
        var delivery = await ClaimedAsync();

        (await runner.RunAsync(delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Succeeded);

        refusing.Answers.ShouldBe(3);
        _store.Attempts.ShouldBe([(delivery.Id, 1, "succeeded")], "the attempt is answered once, not burnt by a busy database");
        var retried = _logs.Entries.Where(entry => entry.EventName == "DeliveryAnswerRetried").ToList();
        retried.Count.ShouldBe(1, "the first retry is logged, then every tenth");
        retried.ShouldAllBe(entry => entry.Message.Contains(typeof(BusyStorageException).FullName!) && !entry.Message.Contains(BusyStorageException.Secret));
    }

    [Fact]
    public async Task A_permanent_storage_refusal_stops_being_retried_a_settle_grace_after_the_first_refusal()
    {
        var refusing = new AnswerRefusingStore(
            _store, refusals: int.MaxValue, () => new BusyStorageException(), onAnswer: () => _clock.Advance(TimeSpan.FromSeconds(10)));
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask, options => options.Timeout = TimeSpan.FromHours(1), port: refusing);

        await Should.ThrowAsync<BusyStorageException>(runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct));

        refusing.Answers.ShouldBe(4, "refused at +10 s, so retries start until +40 s — not for the hour the handler's timeout would allow");
        _logs.Entries.Count(entry => entry.EventName == "DeliveryAnswerRetried").ShouldBe(1);
    }

    [Fact]
    public async Task A_fence_that_no_longer_holds_is_answered_once_and_never_retried()
    {
        var counting = new AnswerRefusingStore(_store, refusals: 0, () => new BusyStorageException());
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask, port: counting);
        var late = await ClaimedAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _store.ClaimAsync(Claim(), Ct);

        (await runner.RunAsync(late, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.LeaseLost);

        counting.Answers.ShouldBe(1);
    }

    [Fact]
    public async Task A_storage_refusal_past_the_lease_is_not_retried()
    {
        var refusing = new AnswerRefusingStore(
            _store, refusals: int.MaxValue, () => new BusyStorageException(), onAnswer: () => _clock.Advance(TimeSpan.FromMinutes(2)));
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask, options => options.Timeout = TimeSpan.FromSeconds(10), port: refusing);
        var delivery = await ClaimedAsync();

        await Should.ThrowAsync<BusyStorageException>(runner.RunAsync(delivery, handler, Claimant, Ct));

        refusing.Answers.ShouldBe(1, "a retry would start after the lease, when another claimant may already hold the delivery");
    }

    [Fact]
    public async Task An_answer_that_fails_is_not_retried_unless_storage_refused_it_and_its_span_still_ends_failed()
    {
        using var spans = new Spans();
        var failing = new AnswerRefusingStore(_store, refusals: int.MaxValue, () => new InvalidOperationException());
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask, port: failing);
        var claimed = await ClaimedEventAsync(traceParent: TraceParent);

        await spans.WithinAsync(() => Should.ThrowAsync<InvalidOperationException>(runner.RunAsync(claimed.Delivery, handler, Claimant, Ct)));

        failing.Answers.ShouldBe(1);
        var span = spans.Recorded.ShouldHaveSingleItem();
        span.GetTagItem("alvo.outcome").ShouldBe("failed");
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_handler_that_ends_within_its_settle_grace_after_its_timeout_is_answered_by_its_own_result()
    {
        var (runner, handler) = Arrange(
            async (AlvoCreated<AlvoRecord> _) => await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None),
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromSeconds(5));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Succeeded);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public void A_settled_handler_is_timed_out_only_when_it_gave_in_to_its_cancellation()
    {
        DeliveryRunner.Settled(Task.CompletedTask).Succeeded.ShouldBeTrue();
        DeliveryRunner.Settled(Task.FromException(new InvalidOperationException())).ShouldBe(DeliveryRunner.AttemptVerdict.Retry(typeof(InvalidOperationException).FullName!));
        DeliveryRunner.Settled(Task.FromException(new AggregateException(AlvoChainDepthExceededException.At(9, 8)))).ShouldBe(DeliveryRunner.AttemptVerdict.Dead("chain-depth-exceeded"));
        DeliveryRunner.Settled(Task.FromCanceled(new CancellationToken(canceled: true))).ShouldBe(DeliveryRunner.AttemptVerdict.Retry("timeout"));
        DeliveryRunner.Settled(Task.FromException(new OperationCanceledException())).ShouldBe(DeliveryRunner.AttemptVerdict.Retry("timeout"));
    }

    private async Task<CapturedLogEntry> LoggedAsync(string eventName)
    {
        for (var wait = 0; wait < 500; wait++)
        {
            if (_logs.Entries.FirstOrDefault(entry => entry.EventName == eventName) is { } line)
            {
                return line;
            }

            await Task.Delay(10, Ct);
        }

        throw new ShouldAssertException($"Nothing was logged as {eventName} within 5 s.");
    }

    private (DeliveryRunner Runner, BoundHandler Handler) Arrange(
        Delegate handle,
        Action<AlvoHandlerOptions>? configure = null,
        TimeProvider? time = null,
        TimeSpan? settle = null,
        IServiceProvider? services = null,
        IDeliveryStore? port = null,
        Action<IAlvoBuilder, Delegate>? register = null)
    {
        var registry = HandlerCatalog.Registry(alvo =>
        {
            if (register is null)
            {
                alvo.Entity("tickets", t => t.OnCreated(Handler, handle, configure: configure));
                return;
            }

            register(alvo, handle);
        });
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>());
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", _catalog);
        var runner = new DeliveryRunner(
            port ?? _store,
            (services ?? new ServiceCollection().BuildServiceProvider()).GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(_data),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            new AbandonedHandlers(Options.Create(new AlvoDeliveryOptions()), _loggers.CreateLogger<AbandonedHandlers>()),
            time ?? _clock,
            _loggers.CreateLogger<DeliveryRunner>())
        {
            SettleGrace = settle ?? DeliveryRunner.LeaseGrace,
        };

        return (runner, binder.For(_catalog).Handler(Handler).ShouldNotBeNull());
    }

    private async Task<ClaimedDelivery> ClaimedAsync() => (await ClaimedEventAsync()).Delivery;

    private async Task<(AlvoEvent Event, ClaimedDelivery Delivery)> ClaimedEventAsync(
        int chainDepth = 0, string? authId = null, string? traceParent = null, string? tenantText = null)
    {
        var @event = new AlvoEvent
        {
            Id = AlvoEventId.Create(_clock.GetUtcNow()),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = _clock.GetUtcNow(),
            Subject = "tickets/1",
            PartitionKey = "tickets:1",
            AuthType = AlvoEventAuthType.User,
            AuthId = authId,
            AuthClaims = "{\"roles\":[\"authenticated\"]}",
            TenantId = tenantText ?? _tenant.ToString(),
            CorrelationId = "corr-1",
            ChainDepth = chainDepth,
            TraceParent = traceParent,
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };
        _store.Publish(@event);
        await _store.FanOutAsync(
            [new DeliveryRequest { EventId = @event.Id, Subscriber = Handler, PartitionKey = @event.PartitionKey, TenantId = @event.TenantId, Kind = DeliveryKind.Handler }],
            Ct);

        return (@event, (await _store.ClaimAsync(Claim(), Ct)).Single());
    }

    private static DeliveryClaim Claim() =>
        new() { Claimant = Claimant, Ordered = [Handler], BatchSize = 10, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) };

    /// <summary>What a provider throws when the database is busy — a <see cref="DbException"/> whose message is never logged.</summary>
    private sealed class BusyStorageException() : DbException($"database is locked; {Secret}", 5)
    {
        internal const string Secret = "Password=hunter2";
    }

    /// <summary>The in-memory store whose answers throw the first <c>refusals</c> times, counting every answer it is asked for.</summary>
    private sealed class AnswerRefusingStore(FakeDeliveryStore inner, int refusals, Func<Exception> refusal, Action? onAnswer = null) : IDeliveryStore
    {
        private int _answers;

        internal int Answers => Volatile.Read(ref _answers);

        public Task EnsureAsync(CancellationToken cancellationToken = default) => inner.EnsureAsync(cancellationToken);

        public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
            inner.FanOutAsync(requests, cancellationToken);

        public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default) =>
            inner.ClaimAsync(claim, cancellationToken);

        public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
            Answer(() => inner.CompleteAsync(attempt, cancellationToken));

        public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
            Answer(() => inner.FailAsync(attempt, failure, cancellationToken));

        public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
            inner.PendingSubscribersAsync(cancellationToken);

        public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
            inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken);

        public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
            inner.CancelPendingAsync(subscriber, cancellationToken);

        public Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);

        public Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.AttemptsAsync(id, cancellationToken);

        public Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
            inner.SelectAsync(filter, cancellationToken);

        public Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default) =>
            inner.RedeliverAsync(id, entry, cancellationToken);

        public Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default) =>
            inner.ReplayAsync(filter, entry, cancellationToken);

        public Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default) =>
            inner.FindOperationAsync(scope, key, cancellationToken);

        private Task<bool> Answer(Func<Task<bool>> answer)
        {
            onAnswer?.Invoke();
            return Interlocked.Increment(ref _answers) <= refusals ? Task.FromException<bool>(refusal()) : answer();
        }
    }

    /// <summary>A scoped service a handler asks for, so a fact can tell its delivery's scope apart and see it disposed.</summary>
    internal sealed class ScopedProbe : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Listens to the hook source under one fact's own root trace.</summary>
    private sealed class Spans : IDisposable
    {
        private const string TestSource = "MMLib.Alvo.Tests.DeliveryRunnerSpans";

        private readonly ActivitySource _test = new(TestSource);
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;
        private ActivityTraceId _trace;

        internal Spans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is AlvoHookTelemetry.SourceName or TestSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == AlvoHookTelemetry.SourceName && activity.TraceId == _trace)
                    {
                        _stopped.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> Recorded => [.. _stopped];

        internal async Task<ActivityTraceId> WithinAsync(Func<Task> act)
        {
            var root = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            _trace = root.TraceId;
            using (_test.StartActivity("fact", ActivityKind.Internal, root))
            {
                await act();
            }

            return root.TraceId;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _test.Dispose();
        }
    }
}
