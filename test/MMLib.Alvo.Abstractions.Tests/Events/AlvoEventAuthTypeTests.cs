using MMLib.Alvo.Events;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary><c>authtype</c> speaks the CloudEvents authcontext vocabulary (spec 2026-10-14 C9, §4.7).</summary>
public class AlvoEventAuthTypeTests
{
    [Theory]
    [InlineData(AlvoActorKind.Unknown, "unknown")]
    [InlineData(AlvoActorKind.Anonymous, "unauthenticated")]
    [InlineData(AlvoActorKind.System, "system")]
    [InlineData(AlvoActorKind.ApiKey, "api_key")]
    [InlineData(AlvoActorKind.User, "user")]
    public void Each_actor_kind_is_reported_as_its_authcontext_value(AlvoActorKind kind, string expected)
        => AlvoEventProvenance.AuthTypeOf(AlvoContext.Anonymous with { ActorKind = kind }).ShouldBe(expected);

    [Fact]
    public void A_host_built_context_with_an_ordinary_user_still_reports_an_api_key()
        => AlvoEventProvenance.AuthTypeOf(new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } })
            .ShouldBe(AlvoEventAuthType.ApiKey);

    [Fact]
    public void An_actor_kind_no_member_names_is_reported_as_unknown_rather_than_guessed()
        => AlvoEventProvenance.AuthTypeOf(AlvoContext.Anonymous with { ActorKind = (AlvoActorKind)42 })
            .ShouldBe(AlvoEventAuthType.Unknown);

    [Fact]
    public void The_values_are_the_authcontext_extensions_own_spelling()
        => new[] { AlvoEventAuthType.ApiKey, AlvoEventAuthType.User, AlvoEventAuthType.System, AlvoEventAuthType.Unauthenticated, AlvoEventAuthType.Unknown }
            .ShouldBe(["api_key", "user", "system", "unauthenticated", "unknown"]);
}
