using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Framework events (spec 2026-10-14 E12, §4.10): only the core and the driver mint <c>alvo.*</c>, through one door.
/// </summary>
public sealed class AlvoFrameworkEventTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_command_completion_is_minted_with_its_payload_and_the_caller_s_stamp()
    {
        var caller = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            Tenant = new TenantId(Guid.NewGuid()),
            ActorKind = AlvoActorKind.User,
            Provenance = new AlvoProvenance { CorrelationId = "corr-1" },
        };

        var @event = AlvoFrameworkEvent.Create(
            "alvo.command.confirm_booking.completed",
            "bookings/42",
            new Dictionary<string, object?> { ["command"] = "confirm_booking" },
            caller,
            _now).Envelope;

        @event.Type.ShouldBe("alvo.command.confirm_booking.completed");
        @event.Subject.ShouldBe("bookings/42");
        @event.Data.Record!["command"].ShouldBe("confirm_booking");
        @event.TenantId.ShouldBe(caller.Tenant!.Value.ToString());
        @event.AuthType.ShouldBe(AlvoEventAuthType.User);
        @event.AuthId.ShouldBe(caller.User.Value.ToString(), "the actor is the caller");
        @event.CorrelationId.ShouldBe("corr-1");
        @event.Time.ShouldBe(_now);
        @event.PartitionKey.ShouldBe(
            $"framework.event:{caller.Tenant}:bookings/42", "one tenant's completions never block another's");
    }

    [Fact]
    public void A_tenantless_completion_is_partitioned_under_the_tenantless_scope() =>
        AlvoFrameworkEvent.Create(
                "alvo.command.quote.completed", "commands/quote", new Dictionary<string, object?>(), AlvoContext.System(null), _now)
            .Envelope.PartitionKey.ShouldBe("framework.event:global:commands/quote");

    [Theory]
    [InlineData("orders.approved")]
    [InlineData("entity.bookings.updated")]
    [InlineData("alvo")]
    [InlineData("alvo.Command.x.completed")]
    public void Anything_outside_the_framework_namespace_is_refused(string type) =>
        Should.Throw<ArgumentException>(
                () => AlvoFrameworkEvent.Create(type, "s", new Dictionary<string, object?>(), AlvoContext.Anonymous, _now))
            .ParamName.ShouldBe("type");

    [Fact]
    public void The_custom_door_still_refuses_the_framework_namespace() =>
        Should.Throw<ArgumentException>(() => AlvoEventName.EnsureCustom("alvo.command.x.completed", "type"));
}
