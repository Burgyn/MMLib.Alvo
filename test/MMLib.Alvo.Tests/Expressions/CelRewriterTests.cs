using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The relation walk refuses a node kind it does not know rather than read it as a leaf, which would hide a relation
/// below it from the public renderer's refusal and from binding.
/// </summary>
public sealed class CelRewriterTests
{
    private static readonly CelNode _alien = new CelBinary(
        CelBinaryOperator.And, new CelLiteral(CelValueType.Bool, true), new Alien(new CelLiteral(CelValueType.Bool, true)));

    [Fact]
    public void An_unknown_node_kind_is_refused_by_the_search() =>
        Should.Throw<InvalidOperationException>(() => CelRewriter.HasRelation(_alien)).Message.ShouldContain("Alien");

    [Fact]
    public void An_unknown_node_kind_is_refused_by_the_rewrite() =>
        Should.Throw<InvalidOperationException>(() => CelRewriter.Replace(_alien, _ => null)).Message.ShouldContain("Alien");

    private sealed record Alien(CelNode Inside) : CelNode;
}
