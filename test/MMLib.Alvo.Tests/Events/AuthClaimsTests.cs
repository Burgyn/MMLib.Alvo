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
}
