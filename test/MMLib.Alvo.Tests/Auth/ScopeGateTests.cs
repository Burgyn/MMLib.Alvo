using MMLib.Alvo.Auth;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Auth;

public class ScopeGateTests
{
    private static AlvoPrincipal PrincipalWith(params string[] scopeTexts)
    {
        var scopes = new HashSet<ApiKeyScope>();
        foreach (var text in scopeTexts)
        {
            ApiKeyScope.TryParse(text, out var scope).ShouldBeTrue();
            scopes.Add(scope);
        }

        return new AlvoPrincipal
        {
            Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } },
            Scopes = scopes,
            KeyId = "dev",
        };
    }

    [Fact]
    public void An_empty_scope_set_denies_every_operation()
    {
        var principal = PrincipalWith();

        new ScopeGate().Allows(principal, "orders", DataOperation.List).ShouldBeFalse();
    }

    [Fact]
    public void A_read_scope_denies_a_write_operation()
    {
        var principal = PrincipalWith("orders:read");

        new ScopeGate().Allows(principal, "orders", DataOperation.Create).ShouldBeFalse();
    }

    [Fact]
    public void A_wildcard_write_scope_allows_delete_on_any_entity()
    {
        var principal = PrincipalWith("*:write");

        new ScopeGate().Allows(principal, "invoices", DataOperation.Delete).ShouldBeTrue();
    }

    [Fact]
    public void A_scope_for_another_entity_denies_this_entity()
    {
        var principal = PrincipalWith("orders:read");

        new ScopeGate().Allows(principal, "invoices", DataOperation.List).ShouldBeFalse();
    }
}
