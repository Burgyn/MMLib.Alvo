using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>The steps half of the delivery protocol: memoized step results, kept per (event, subscriber, step).</summary>
public abstract partial class DeliveryStoreContractTests
{
    private static readonly DateTimeOffset _stepCompletedAt = new(2026, 11, 6, 9, 30, 15, 123, 456, TimeSpan.Zero);

    /// <summary>A recorded step is read back with its result and completion instant.</summary>
    [Fact]
    public async Task A_recorded_step_is_read_back_with_its_result()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        var recorded = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "\"deposit-1\""), Ct);

        recorded.ShouldBe(Step(@event, Confirm, "charge", "\"deposit-1\""));
        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().ShouldBe(recorded);
    }

    /// <summary>The first record of a step wins: a second record of the same step keeps, and returns, the first result.</summary>
    [Fact]
    public async Task A_second_record_of_one_step_keeps_the_first_result()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        var first = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "1"), Ct);
        var second = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "2"), Ct);

        (first.Result, second.Result).ShouldBe(("1", "1"));
        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().Result.ShouldBe("1");
    }

    /// <summary>Two subscribers of one event keep their own step of the same name.</summary>
    [Fact]
    public async Task Steps_are_kept_per_subscriber_of_one_event()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "\"confirm\""), Ct);
        await world.Store.RecordStepAsync(Step(@event, Audit, "charge", "\"audit\""), Ct);

        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().Result.ShouldBe("\"confirm\"");
        (await world.Store.StepsAsync(@event, Audit, Ct)).ShouldHaveSingleItem().Result.ShouldBe("\"audit\"");
    }

    /// <summary>A read of one event's steps returns none of another event's.</summary>
    [Fact]
    public async Task Steps_of_another_event_are_not_read()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);

        await world.Store.RecordStepAsync(Step(first, Confirm, "charge", "null"), Ct);
        await world.Store.RecordStepAsync(Step(first, Confirm, "email", "null"), Ct);

        (await world.Store.StepsAsync(first, Confirm, Ct)).Select(step => step.Name).ShouldBe(["charge", "email"], ignoreOrder: true);
        (await world.Store.StepsAsync(second, Confirm, Ct)).ShouldBeEmpty();
    }

    private static DeliveryStep Step(Guid @event, string subscriber, string name, string result) =>
        new() { EventId = @event, Subscriber = subscriber, Name = name, Result = result, CompletedAt = _stepCompletedAt };
}
