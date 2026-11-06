using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Events;

/// <summary>After the commit, a condition's <c>now()</c> is the event's time — never the delivery's (HX-11 Ruling 1).</summary>
public sealed class HandlerConditionNowTests
{
    private static readonly DateTimeOffset _time = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly BoundCondition _createdWhenItHappened = BoundCondition.Of(CelFixtures.CompileCondition("now() == new.created_at"));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void A_handler_s_now_is_the_event_s_time(int secondsLater, bool matches)
    {
        var judgement = HandlerCondition.Judge(
            _createdWhenItHappened, Event(_time.AddSeconds(secondsLater)), CelFixtures.Evaluator, new EnvelopeCaller(AlvoContext.Anonymous, false));

        judgement.ShouldBe(matches ? HandlerJudgement.Match : HandlerJudgement.NoMatch);
        AlvoFlow.Instant.ShouldBeNull();
    }

    [Fact]
    public void The_event_s_time_overrides_an_instant_the_delivery_flow_carries()
    {
        using var delivery = AlvoFlow.EnterInstant(_time.AddHours(1));

        HandlerCondition.Judge(_createdWhenItHappened, Event(_time), CelFixtures.Evaluator, new EnvelopeCaller(AlvoContext.Anonymous, false))
            .ShouldBe(HandlerJudgement.Match);
        AlvoFlow.Instant.ShouldBe(_time.AddHours(1));
    }

    private static AlvoEvent Event(DateTimeOffset time) => new()
    {
        Id = AlvoEventId.Create(time),
        Source = AlvoEvent.DefaultSource,
        Type = "orders.created",
        Time = time,
        Subject = "orders/1",
        PartitionKey = "orders:1",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "c",
        Data = new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["created_at"] = _time }) },
    };
}
