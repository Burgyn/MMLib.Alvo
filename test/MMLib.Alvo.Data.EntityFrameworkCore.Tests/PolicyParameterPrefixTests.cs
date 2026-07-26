namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class PolicyParameterPrefixTests
{
    [Fact]
    public void No_reserved_name_starts_with_ef_cores_own_parameter_letter()
        => PolicyParameterPrefix.All.ShouldAllBe(name => !name.StartsWith('p'));

    [Fact]
    public void Every_reserved_name_starts_with_the_reserved_alvo_word()
        => PolicyParameterPrefix.All.ShouldAllBe(name => name.StartsWith("alvo_", StringComparison.Ordinal));

    /// <summary>
    /// Generated names are a prefix plus an ordinal, so one prefix being a prefix of another would make
    /// <c>alvo_f1</c> and <c>alvo_f</c>+<c>1</c> collide across two independently numbered families.
    /// </summary>
    [Fact]
    public void No_reserved_name_is_a_prefix_of_another()
    {
        foreach (var name in PolicyParameterPrefix.All)
        {
            PolicyParameterPrefix.All
                .Where(other => !string.Equals(other, name, StringComparison.Ordinal))
                .ShouldAllBe(other => !other.StartsWith(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_three_policy_predicates_have_three_distinct_prefixes()
        => new[] { PolicyParameterPrefix.Using, PolicyParameterPrefix.WithCheck, PolicyParameterPrefix.TenantScope }
            .Distinct(StringComparer.Ordinal).Count().ShouldBe(3);

    /// <summary>
    /// Every declared name is in <see cref="PolicyParameterPrefix.All"/>. Without this the disjointness
    /// invariants above would pass vacuously for a name someone forgot to list — which is exactly the
    /// name that would then be free to collide.
    /// </summary>
    [Fact]
    public void Every_declared_name_is_covered_by_the_invariants()
        => PolicyParameterPrefix.All.ShouldBe(
            [
                PolicyParameterPrefix.Using,
                PolicyParameterPrefix.WithCheck,
                PolicyParameterPrefix.TenantScope,
                PolicyParameterPrefix.Filter,
                PolicyParameterPrefix.Keyset,
                PolicyParameterPrefix.RowId,
            ],
            ignoreOrder: true);
}
