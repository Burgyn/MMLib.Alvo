using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>The identity package's Abstractions grant is for the framework-table names, never for the ambient flow.</summary>
public sealed class InternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced =
        InternalMemberReferences.Of(typeof(AlvoIdentityContextResolver).Assembly);

    /// <summary>The control: the scan sees a member reference this assembly certainly makes.</summary>
    [Fact]
    public void The_scan_sees_the_context_the_resolver_builds()
        => _referenced.ShouldContain("MMLib.Alvo.AlvoContext::set_User");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));

    public static TheoryData<string> SecurityCoreMembers() => [.. InternalMemberReferences.SecurityCoreOnly];

    [Theory]
    [MemberData(nameof(SecurityCoreMembers))]
    public void It_never_reaches_a_security_core_member(string member)
        => _referenced.ShouldNotContain(member);
}
