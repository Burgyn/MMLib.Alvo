using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests;

/// <summary><c>MMLib.Alvo.Testing</c> holds an Abstractions grant for the reachability port; it never sets the ambient flow.</summary>
public sealed class TestingInternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(InMemoryAlvoData).Assembly);

    [Fact]
    public void The_scan_sees_the_idempotency_guard_the_double_calls()
        => _referenced.ShouldContain("MMLib.Alvo.Data.AlvoIdempotency::EnsureUsableToken");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));
}
