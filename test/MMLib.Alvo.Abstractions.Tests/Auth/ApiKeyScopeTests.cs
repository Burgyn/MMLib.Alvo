using MMLib.Alvo.Auth;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Abstractions.Tests.Auth;

public class ApiKeyScopeTests
{
    [Theory]
    [InlineData("orders:read", DataOperation.List, true)]
    [InlineData("orders:read", DataOperation.Get, true)]
    [InlineData("orders:read", DataOperation.Update, false)]
    [InlineData("orders:write", DataOperation.Create, true)]
    [InlineData("orders:write", DataOperation.Delete, true)]
    [InlineData("orders:write", DataOperation.List, false)]
    [InlineData("*:read", DataOperation.Get, true)]
    public void Scope_gates_the_operation_it_names(string scope, DataOperation operation, bool allowed)
    {
        ApiKeyScope.TryParse(scope, out var parsed).ShouldBeTrue();

        parsed.Allows("orders", operation).ShouldBe(allowed);
    }

    [Fact]
    public void A_scope_for_another_entity_never_allows_this_one()
    {
        ApiKeyScope.TryParse("invoices:read", out var scope).ShouldBeTrue();

        scope.Allows("orders", DataOperation.List).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("orders:admin")]
    [InlineData("orders:read:extra")]
    [InlineData(":read")]
    public void Malformed_scopes_are_refused_rather_than_widened(string scope)
    {
        ApiKeyScope.TryParse(scope, out _).ShouldBeFalse();
    }
}
