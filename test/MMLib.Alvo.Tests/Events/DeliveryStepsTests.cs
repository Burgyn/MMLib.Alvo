using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Scheduling;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>One attempt's steps over the in-memory store: memoized, first-writer-wins, and bounded.</summary>
public sealed class DeliveryStepsTests
{
    private const string Subscriber = "confirm-booking";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(_now);
    private readonly FakeDeliveryStore _store;
    private readonly Guid _event = Guid.NewGuid();

    public DeliveryStepsTests() => _store = new FakeDeliveryStore(_clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_completed_step_returns_its_stored_value_without_running()
    {
        await _store.RecordStepAsync(Stored("charge", "\"deposit-1\""), Ct);
        var ran = 0;

        var value = await Steps().RunAsync("charge", _ => Task.FromResult($"deposit-{++ran + 1}"), Ct);

        (value, ran).ShouldBe(("deposit-1", 0));
    }

    [Fact]
    public async Task A_racing_record_returns_the_winners_value()
    {
        var steps = Steps();

        var value = await steps.RunAsync("charge", async ct =>
        {
            await _store.RecordStepAsync(Stored("charge", "\"winner\""), ct);
            return "loser";
        }, Ct);

        value.ShouldBe("winner");
        (await _store.StepsAsync(_event, Subscriber, Ct)).ShouldHaveSingleItem().Result.ShouldBe("\"winner\"");
    }

    [Fact]
    public async Task A_result_over_the_cap_is_refused()
    {
        var huge = new string('x', DeliverySteps.MaxResultBytes);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => Steps().RunAsync("big", _ => Task.FromResult(huge), Ct));

        refused.Message.ShouldContain("256 KiB");
        (await _store.StepsAsync(_event, Subscriber, Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("")]
    public async Task A_name_with_a_control_character_is_refused(string name)
    {
        await Should.ThrowAsync<ArgumentException>(() => Steps().RunAsync(name, _ => Task.FromResult(1), Ct));
    }

    [Fact]
    public async Task A_name_longer_than_200_characters_is_refused()
    {
        await Should.ThrowAsync<ArgumentException>(() => Steps().RunAsync(new string('a', 201), _ => Task.CompletedTask, Ct));
        await Steps().RunAsync(new string('a', 200), _ => Task.CompletedTask, Ct);
    }

    [Fact]
    public async Task The_1001st_step_of_one_run_is_refused()
    {
        var steps = Steps();
        for (var step = 0; step < DeliverySteps.MaxSteps; step++)
        {
            await steps.RunAsync($"s{step}", _ => Task.CompletedTask, Ct);
        }

        await Should.ThrowAsync<InvalidOperationException>(() => steps.RunAsync("one-more", _ => Task.CompletedTask, Ct));
    }

    [Fact]
    public async Task A_step_with_no_value_is_stored_as_json_null()
    {
        await Steps().RunAsync("notify", _ => Task.CompletedTask, Ct);

        var stored = (await _store.StepsAsync(_event, Subscriber, Ct)).ShouldHaveSingleItem();
        (stored.Result, stored.CompletedAt).ShouldBe(("null", _now));
    }

    [Fact]
    public async Task A_sleep_until_a_passed_instant_returns_at_once_even_on_its_first_call()
    {
        var steps = Steps();

        await steps.SleepUntilAsync("past", _now.AddMinutes(-1), Ct);

        steps.SleepRequested.ShouldBeNull();
        (await _store.StepsAsync(_event, Subscriber, Ct)).ShouldHaveSingleItem().Name.ShouldBe("past");
    }

    [Fact]
    public async Task A_sleep_until_a_later_instant_records_it_flags_the_run_and_ends_it()
    {
        var steps = Steps();

        await Should.ThrowAsync<StepSleep>(() => steps.SleepUntilAsync("later", _now.AddHours(2).ToOffset(TimeSpan.FromHours(2)), Ct));

        steps.SleepRequested.ShouldBe(_now.AddHours(2));
    }

    [Fact]
    public async Task A_resumed_sleep_returns_once_its_stored_wake_time_passed_and_sleeps_again_before()
    {
        await _store.RecordStepAsync(Stored("wait", "\"2026-11-06T11:00:00+00:00\""), Ct);

        var early = Steps();
        await Should.ThrowAsync<StepSleep>(() => early.SleepUntilAsync("wait", _now.AddYears(1), Ct));
        early.SleepRequested.ShouldBe(_now.AddHours(2), "the stored wake time wins over the one asked for now");

        _clock.Advance(TimeSpan.FromHours(2));
        var resumed = Steps();
        await resumed.SleepUntilAsync("wait", _now.AddYears(1), Ct);
        resumed.SleepRequested.ShouldBeNull();
    }

    [Fact]
    public async Task The_pass_through_runs_work_every_time_and_never_sleeps()
    {
        var context = AlvoEventContext.Create(Event(), Substitute.For<IAlvoSession>(), Subscriber);
        var ran = 0;

        await context.Steps.RunAsync("charge", _ => Task.FromResult(++ran), Ct);
        await context.Steps.RunAsync("charge", _ => Task.FromResult(++ran), Ct);
        await context.Steps.SleepUntilAsync("wait", _now.AddYears(1), Ct);

        ran.ShouldBe(2);
        context.Steps.ShouldBeSameAs(PassThroughSteps.Instance);
    }

    [Fact]
    public async Task A_job_context_carries_steps()
    {
        var steps = Steps();
        var context = new AlvoEventContext(Event(), Subscriber, 1, Substitute.For<IAlvoSession>(), steps);
        var services = new ServiceCollection().AddSingleton<StepsProbeJob>().BuildServiceProvider();
        var payload = new AlvoRecord(new Dictionary<string, object?> { [JobInvocation.ScheduledForKey] = _now });

        await JobInvocation.InvokeAsync<StepsProbeJob>(services, payload, context, Ct);

        services.GetService(typeof(StepsProbeJob)).ShouldBeOfType<StepsProbeJob>().Seen.ShouldBeSameAs(steps);
        AlvoJobContext.Create(Subscriber, _now, null, Substitute.For<IAlvoSession>()).Steps.ShouldBeSameAs(PassThroughSteps.Instance);
    }

    private DeliverySteps Steps() => new(_store, _event, Subscriber, _clock);

    private DeliveryStep Stored(string name, string result) =>
        new() { EventId = _event, Subscriber = Subscriber, Name = name, Result = result, CompletedAt = _now };

    private static AlvoEvent Event() => new()
    {
        Id = Guid.CreateVersion7(),
        Source = AlvoEvent.DefaultSource,
        Type = "test.delivery",
        Time = _now,
        Subject = "test",
        PartitionKey = "test",
        AuthType = AlvoEventAuthType.Unknown,
        CorrelationId = "c-1",
        Data = new AlvoEventData(),
    };

    private sealed class StepsProbeJob : IAlvoScheduledJob
    {
        internal IAlvoSteps? Seen { get; private set; }

        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
        {
            Seen = context.Steps;
            return Task.CompletedTask;
        }
    }
}
