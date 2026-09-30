namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The Abstractions grant D45 gives this package is used for the trace seam and nothing else: the agent never builds an
/// allow decision or a compiled expression of its own.
/// </summary>
public sealed class InternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(AlvoAssistant).Assembly);

    /// <summary>The control: the scan sees an internal member reference — the one the grant exists for.</summary>
    [Fact]
    public void The_agent_references_the_internal_trace_update_it_was_granted() =>
        _referenced.ShouldContain("MMLib.Alvo.Ai.AssistantUpdate/TurnTraced::.ctor");

    [Theory]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::.ctor")]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::Allow")]
    [InlineData("MMLib.Alvo.Expressions.CompiledExpression::.ctor")]
    public void The_agent_never_reaches_a_security_core_member(string member)
    {
        InternalMemberReferences.SecurityCoreOnly.ShouldContain(member);
        _referenced.ShouldNotContain(member);
    }
}
