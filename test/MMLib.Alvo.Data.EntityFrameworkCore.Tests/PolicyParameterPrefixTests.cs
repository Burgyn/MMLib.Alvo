using System.Reflection;

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
    /// Every literal this type declares is in <see cref="PolicyParameterPrefix.All"/>. Comparing
    /// <c>All</c> against a hand-written list of the same constants would not close this: a seventh
    /// constant added later and forgotten in <c>All</c> would be forgotten in the list too, and the new
    /// name would escape every invariant above — the exact scenario those invariants exist for. Reflection
    /// is what makes the set self-maintaining.
    /// </summary>
    [Fact]
    public void Every_declared_constant_is_covered_by_the_invariants()
        => DeclaredConstants().ShouldBe(PolicyParameterPrefix.All, ignoreOrder: true);

    /// <summary>
    /// <c>alvo_p</c> is <c>IPredicateRenderer.Render</c>'s shipped default prefix — the name a forgotten
    /// explicit prefix actually produces — so the reserved set has to stay disjoint from it as well as from
    /// EF's own <c>pN</c> family. It is deliberately <em>not</em> one of Alvo's reserved names: a predicate
    /// rendered with the default is a bug, and a name that cannot collide with a reserved one is what lets
    /// the binder's duplicate check see it.
    /// </summary>
    [Fact]
    public void No_reserved_name_collides_with_the_renderers_default_prefix()
        => PolicyParameterPrefix.All.ShouldAllBe(name =>
            !name.StartsWith(RendererDefaultPrefix, StringComparison.Ordinal)
            && !RendererDefaultPrefix.StartsWith(name, StringComparison.Ordinal));

    private const string RendererDefaultPrefix = "alvo_p";

    private static IReadOnlyList<string> DeclaredConstants() =>
    [
        .. typeof(PolicyParameterPrefix)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];
}
