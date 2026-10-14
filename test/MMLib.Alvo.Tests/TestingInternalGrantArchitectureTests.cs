using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests;

/// <summary>
/// <c>MMLib.Alvo.Testing</c> holds an Abstractions grant for the reachability port; it never sets the ambient flow nor
/// reaches a security-core member.
/// </summary>
public sealed class TestingInternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(InMemoryAlvoData).Assembly);

    [Fact]
    public void The_scan_sees_the_idempotency_guard_the_double_calls()
        => _referenced.ShouldContain("MMLib.Alvo.Data.AlvoIdempotency::EnsureUsableToken");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));

    public static TheoryData<string> SecurityCoreMembers() => [.. InternalMemberReferences.SecurityCoreOnly];

    [Theory]
    [MemberData(nameof(SecurityCoreMembers))]
    public void It_never_reaches_a_security_core_member(string member)
        => _referenced.ShouldNotContain(member);
}
