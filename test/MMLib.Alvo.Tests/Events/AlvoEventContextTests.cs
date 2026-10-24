using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The public factory a class handler's unit test calls (spec 2026-10-14 HX-03 settled details (53)).</summary>
public sealed class AlvoEventContextTests
{
    private static readonly AlvoEvent _event = new()
    {
        Id = Guid.CreateVersion7(),
        Source = AlvoEvent.DefaultSource,
        Type = "test.delivery",
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = "test",
        PartitionKey = "test",
        AuthType = AlvoEventAuthType.Unknown,
        CorrelationId = "c-1",
        Data = new AlvoEventData(),
    };

    [Fact]
    public void Create_hands_back_what_it_was_given()
    {
        var session = Substitute.For<IAlvoSession>();

        var context = AlvoEventContext.Create(_event, session, "send-confirmation", attempt: 3);

        (context.Event, context.EventId, context.Data, context.Subscriber, context.Attempt).ShouldBe((_event, _event.Id, session, "send-confirmation", 3));
    }

    [Fact]
    public void An_attempt_below_one_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoEventContext.Create(_event, Substitute.For<IAlvoSession>(), "x", attempt: 0));

    [Fact]
    public void A_blank_subscriber_is_refused() =>
        Should.Throw<ArgumentException>(() => AlvoEventContext.Create(_event, Substitute.For<IAlvoSession>(), " "));
}
