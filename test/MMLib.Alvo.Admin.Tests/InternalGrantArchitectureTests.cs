using MMLib.Alvo.Ai.Tests;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The Abstractions grant D45 gives the dashboard is used for the trace seam and nothing else: it never builds an allow
/// decision or a compiled expression of its own.
/// </summary>
public sealed class InternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced =
        InternalMemberReferences.Of(typeof(MMLib.Alvo.Admin.Components.Assistant.AssistantDrawer).Assembly);

    /// <summary>The control: the scan sees the internal member the grant exists for.</summary>
    [Fact]
    public void The_dashboard_references_the_internal_trace_request_it_was_granted() =>
        _referenced.ShouldContain("MMLib.Alvo.Ai.AssistantRequest::set_IncludeTrace");

    [Theory]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::.ctor")]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::Allow")]
    [InlineData("MMLib.Alvo.Expressions.CompiledExpression::.ctor")]
    [InlineData("MMLib.Alvo.Identity.Role::Application")]
    public void The_dashboard_never_reaches_a_security_core_member(string member)
    {
        InternalMemberReferences.SecurityCoreOnly.ShouldContain(member);
        _referenced.ShouldNotContain(member);
    }
}
