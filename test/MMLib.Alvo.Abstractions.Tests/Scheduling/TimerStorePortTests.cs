using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Reflection;

namespace MMLib.Alvo.Abstractions.Tests.Scheduling;

/// <summary>
/// <see cref="ITimerStore"/> is resolvable from the container, so its shape is what keeps a host from appending a framework
/// event or choosing a timer's provenance through it.
/// </summary>
public sealed class TimerStorePortTests
{
    [Fact]
    public void A_host_built_schedule_event_cannot_reach_the_outbox_through_ITimerStore()
    {
        var eventParameters = typeof(ITimerStore).GetMethods().SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType).Concat(typeof(ScheduleAdvance).GetProperties().Select(property => property.PropertyType))
            .ToList();

        eventParameters.ShouldNotContain(typeof(AlvoEvent));
        eventParameters.ShouldNotContain(typeof(IReadOnlyList<AlvoEvent>));
        typeof(AlvoFrameworkEnvelope).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        typeof(AlvoFrameworkEnvelope).GetMethods(BindingFlags.Public | BindingFlags.Static).ShouldBeEmpty();
        typeof(TimerEntry).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        typeof(TimerEntry).GetMethods(BindingFlags.Public | BindingFlags.Static).ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => AlvoCustomEvent.Create(HostBuilt("alvo.schedule.nightly_roll")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public void A_malformed_timer_key_is_refused(string key) =>
        Should.Throw<ArgumentException>(() => TimerEntry.Of("", key, DateTimeOffset.UnixEpoch, Message(), AlvoProvenance.None))
            .ParamName.ShouldBe("key");

    [Fact]
    public void A_timer_key_of_two_hundred_characters_is_accepted_and_one_more_is_refused()
    {
        TimerEntry.Of("", new string('k', 200), DateTimeOffset.UnixEpoch, Message(), AlvoProvenance.None).Key.Length.ShouldBe(200);
        Should.Throw<ArgumentException>(() => TimerEntry.Of("", new string('k', 201), DateTimeOffset.UnixEpoch, Message(), AlvoProvenance.None));
    }

    [Fact]
    public void A_timer_is_stored_in_utc_with_its_message_and_its_provenance()
    {
        var provenance = new AlvoProvenance { CorrelationId = "corr-1", CausationId = "cause-1", Depth = 3, TraceParent = "00-a-b-01" };

        var timer = TimerEntry.Of("tenant-a", "remind-42", new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.FromHours(2)), Message(), provenance);

        timer.DueAt.ShouldBe(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        timer.DueAt.Offset.ShouldBe(TimeSpan.Zero);
        timer.MessageType.ShouldBe("reminders.due");
        AlvoEventJson.Read(timer.Payload).Data.Record!["booking"].ShouldBe("42");
        TimerProvenance.Read(timer.Provenance).ShouldBe(provenance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"depth\":-1}")]
    [InlineData("{\"depth\":\"deep\"}")]
    [InlineData("[]")]
    public void Malformed_stored_provenance_reads_as_none(string json)
    {
        TimerProvenance.Read(json).ShouldBe(AlvoProvenance.None);
        TimerProvenance.TryRead(json).ShouldBeNull();
    }

    private static AlvoCustomEvent Message() => AlvoCustomEnvelope.Build(
        "reminders.due", "bookings/42", new Dictionary<string, object?> { ["booking"] = "42" }, AlvoContext.System(null), DateTimeOffset.UnixEpoch);

    private static AlvoEvent HostBuilt(string type) => new()
    {
        Id = Guid.NewGuid(),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = DateTimeOffset.UnixEpoch,
        Subject = "schedules/nightly_roll",
        PartitionKey = "framework.event:global:schedules/nightly_roll",
        AuthType = AlvoEventAuthType.System,
        CorrelationId = "forged",
        Data = new AlvoEventData(),
    };
}
