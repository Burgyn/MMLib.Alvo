using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests;

/// <summary>
/// <c>MMLib.Alvo.Testing</c> holds an Abstractions grant for the reachability port; it never sets the ambient flow — save
/// the instant, by name (HX-11 Ruling 48) — nor reaches a security-core member.
/// </summary>
public sealed class TestingInternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(InMemoryAlvoData).Assembly);

    /// <summary>
    /// The two instant setters this assembly may call (HX-11 Ruling 48): <c>MMLib.Alvo.Testing</c> is never shipped, and its
    /// <see cref="InMemoryAlvoData"/> — the reference implementation the contract suites drive — binds an instant at its
    /// entry points exactly as the shipped data port does, so a <c>now()</c> rule evaluates in memory as it does in SQL.
    /// </summary>
    private static readonly IReadOnlySet<string> _instantSetters =
        new HashSet<string>(StringComparer.Ordinal) { "MMLib.Alvo.AlvoFlow::EnterInstant", "MMLib.Alvo.AlvoFlow::EnsureInstant" };

    [Fact]
    public void The_scan_sees_the_idempotency_guard_the_double_calls()
        => _referenced.ShouldContain("MMLib.Alvo.Data.AlvoIdempotency::EnsureUsableToken");

    [Fact]
    public void It_never_sets_the_ambient_flow_beyond_the_instant()
        => InternalMemberReferences.FlowSetters.Except(_instantSetters).ShouldAllBe(member => !_referenced.Contains(member));

    [Fact]
    public void The_exempt_instant_setters_are_flow_setters()
        => _instantSetters.ShouldBeSubsetOf(InternalMemberReferences.FlowSetters);

    public static TheoryData<string> SecurityCoreMembers() => [.. InternalMemberReferences.SecurityCoreOnly];

    [Theory]
    [MemberData(nameof(SecurityCoreMembers))]
    public void It_never_reaches_a_security_core_member(string member)
        => _referenced.ShouldNotContain(member);
}
