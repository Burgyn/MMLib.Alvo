namespace MMLib.Alvo.Abstractions.Tests.Identity;

/// <summary>
/// The two provenance properties an <see cref="AlvoContext"/> carries: how the caller authenticated, and which flow it
/// belongs to (spec 2026-10-14 §4.6, §4.7 <em>HX-01 settled details</em>).
/// </summary>
public class AlvoContextProvenanceTests
{
    [Fact]
    public void The_anonymous_caller_is_derived_as_anonymous()
        => AlvoContext.Anonymous.ActorKind.ShouldBe(AlvoActorKind.Anonymous);

    [Fact]
    public void The_system_caller_is_derived_as_system_whatever_its_tenant()
    {
        AlvoContext.System(tenant: null).ActorKind.ShouldBe(AlvoActorKind.System);
        AlvoContext.System(TenantId.New()).ActorKind.ShouldBe(AlvoActorKind.System);
    }

    [Fact]
    public void A_host_built_context_with_an_ordinary_user_is_derived_as_an_api_key()
        => new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } }
            .ActorKind.ShouldBe(AlvoActorKind.ApiKey, "host-built and third-party contexts keep today's meaning");

    [Fact]
    public void An_explicit_actor_kind_wins_over_the_derived_one()
        => new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated }, ActorKind = AlvoActorKind.User }
            .ActorKind.ShouldBe(AlvoActorKind.User);

    [Fact]
    public void Unknown_is_only_ever_set_explicitly()
        => (AlvoContext.Anonymous with { ActorKind = AlvoActorKind.Unknown }).ActorKind.ShouldBe(AlvoActorKind.Unknown);

    [Fact]
    public void Changing_the_user_of_a_derived_context_re_derives_its_kind()
        => (AlvoContext.Anonymous with { User = UserId.New() }).ActorKind.ShouldBe(AlvoActorKind.ApiKey);

    [Fact]
    public void A_context_carries_no_provenance_until_one_is_given()
    {
        var provenance = AlvoContext.Anonymous.Provenance;

        provenance.ShouldBeSameAs(AlvoProvenance.None);
        provenance.Depth.ShouldBe(0);
        provenance.CorrelationId.ShouldBeNull();
        provenance.CausationId.ShouldBeNull();
    }

    [Fact]
    public void A_negative_depth_is_refused_where_it_is_built()
        => Should.Throw<ArgumentOutOfRangeException>(() => new AlvoProvenance { Depth = -1 })
            .ParamName.ShouldBe(nameof(AlvoProvenance.Depth));

    [Fact]
    public void A_null_provenance_is_refused_rather_than_read_as_none()
        => Should.Throw<ArgumentNullException>(() => AlvoContext.Anonymous with { Provenance = null! });
}
