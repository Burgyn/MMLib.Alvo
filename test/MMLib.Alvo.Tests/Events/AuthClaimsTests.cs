using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary><c>authclaims</c>: the caller's roles as the authcontext extension's JSON string, and its tolerant reader.</summary>
public class AuthClaimsTests
{
    [Fact]
    public void The_claims_name_every_role_in_ordinal_order()
        => AlvoEventProvenance.AuthClaimsOf(new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        }).ShouldBe("""{"roles":["admin","authenticated"]}""");

    [Fact]
    public void The_reader_returns_what_the_writer_wrote()
        => AlvoEventProvenance.RolesIn(AlvoEventProvenance.AuthClaimsOf(AlvoContext.System(tenant: null)))
            .ShouldBe(["admin"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"teams":["a"]}""")]
    [InlineData("""{"roles":"admin"}""")]
    [InlineData("""{"roles":["admin",1]}""")]
    public void Anything_but_a_list_of_role_names_reads_as_absent(string? claims)
        => AlvoEventProvenance.RolesIn(claims).ShouldBeNull();

    [Fact]
    public void A_caller_without_tenant_roles_writes_exactly_today_s_claims()
    {
        var caller = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            Tenant = new TenantId(Guid.NewGuid()),
        };

        AlvoEventProvenance.AuthClaimsOf(caller).ShouldBe("""{"roles":["authenticated"]}""");
        AlvoEventProvenance.AuthClaimsOf(caller with { Tenant = null, TenantRoles = Tenanted("dispatcher") })
            .ShouldBe("""{"roles":["authenticated"]}""", "tenant roles without a tenant count nowhere, so they are not recorded");
    }

    [Fact]
    public void The_tenant_roles_round_trip_beside_the_roles()
    {
        var claims = AlvoEventProvenance.AuthClaimsOf(new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            Tenant = new TenantId(Guid.NewGuid()),
            TenantRoles = Tenanted("manager", "dispatcher"),
        });

        claims.ShouldBe("""{"roles":["authenticated"],"tenantRoles":["dispatcher","manager"]}""");
        AlvoEventProvenance.RolesIn(claims).ShouldBe(["authenticated"]);
        AlvoEventProvenance.TenantRolesIn(claims).ShouldBe(["dispatcher", "manager"]);
    }

    [Fact]
    public void Claims_without_tenant_roles_read_as_none() =>
        AlvoEventProvenance.TenantRolesIn("""{"roles":["authenticated"]}""").ShouldBeNull();

    public static TheoryData<string> DamagedTenantRoles { get; } = new()
    {
        """{"roles":[],"tenantRoles":"x"}""",
        """{"roles":[],"tenantRoles":[1]}""",
        "{\"roles\":[],\"tenantRoles\":[\"" + new string('\uD800', 1) + "\"]}",
    };

    [Theory]
    [MemberData(nameof(DamagedTenantRoles))]
    public void A_damaged_tenant_roles_member_reads_as_absent(string claims) =>
        AlvoEventProvenance.TenantRolesIn(claims).ShouldBeNull();

    [Fact]
    public void A_claims_string_holding_a_lone_surrogate_reads_as_absent()
        => AlvoEventProvenance.RolesIn("{\"roles\":[\"" + new string('\uD800', 1) + "\"]}").ShouldBeNull();

    private static IReadOnlySet<Role> Tenanted(params string[] names) => RoleCatalog.Create(names).Resolve(names);
}
