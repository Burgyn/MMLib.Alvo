using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The guided condition's scope is read off one working copy and kept on it, never process-wide (final review M4).</summary>
public sealed class ConditionScopeTests
{
    private const string Shop = """
        { "apiVersion": "alvo.dev/v1", "name": "x", "auth": { "roles": ["admin"] },
          "entities": { "orders": { "fields": { "total": { "type": "decimal" } } } } }
        """;

    [Fact]
    public void A_scope_asked_again_over_an_unchanged_copy_is_the_one_already_read()
    {
        var copy = Copy(Shop);

        ConditionScope.Of(copy, "orders").ShouldBeSameAs(ConditionScope.Of(copy, "orders"));
    }

    [Fact]
    public void Each_copy_keeps_its_own_scope()
    {
        var first = Copy(Shop);
        var second = Copy(Shop.Replace("\"admin\"", "\"clerk\"", StringComparison.Ordinal));

        var read = ConditionScope.Of(first, "orders");
        ConditionScope.Of(second, "orders").Roles.ShouldBe(["clerk"]);

        ConditionScope.Of(first, "orders").ShouldBeSameAs(read, "another copy's read does not displace this one's");
        first.ConditionScopes.ShouldNotBeSameAs(second.ConditionScopes);
    }

    [Fact]
    public void An_edit_to_the_copy_is_read_again()
    {
        var copy = Copy(Shop);
        var before = ConditionScope.Of(copy, "orders");

        copy.Replace(Shop.Replace("\"admin\"", "\"admin\", \"clerk\"", StringComparison.Ordinal)).ShouldBeTrue();

        ConditionScope.Of(copy, "orders").Roles.ShouldBe(["admin", "clerk"]);
        before.Roles.ShouldBe(["admin"]);
    }

    private static WorkingCopy Copy(string json)
    {
        var copy = new WorkingCopy();
        copy.Take(json, revision: 1);
        return copy;
    }
}
