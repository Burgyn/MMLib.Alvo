using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Rules.Internal;
using System.Reflection;

namespace MMLib.Alvo.Tests;

/// <summary>
/// The controls the ban lists rest on: every banned name is a member Abstractions really declares, spelled as the scan
/// spells it, so a fact that a granted assembly never reaches it cannot pass because the name matches nothing (#397).
/// </summary>
public sealed class InternalMemberReferencesTests
{
    private const BindingFlags EveryMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static TheoryData<string> BannedMembers() =>
        [.. InternalMemberReferences.SecurityCoreOnly, .. InternalMemberReferences.FlowSetters];

    [Theory]
    [MemberData(nameof(BannedMembers))]
    public void Every_banned_name_is_a_member_abstractions_declares(string member)
    {
        var (typeName, memberName) = Split(member);
        var type = typeof(AlvoContext).Assembly.GetType(typeName.Replace('/', '+'), throwOnError: false);

        type.ShouldNotBeNull($"'{typeName}' is not a type of MMLib.Alvo.Abstractions.");
        type.GetMember(memberName, EveryMember).ShouldNotBeEmpty();
    }

    /// <summary>The scan spells a banned member exactly as the list does: the core, which may, builds both.</summary>
    [Theory]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::Allow")]
    [InlineData("MMLib.Alvo.Expressions.CompiledExpression::.ctor")]
    public void The_scan_sees_the_core_reach_a_security_core_member(string member)
        => InternalMemberReferences.Of(typeof(PolicyEngine).Assembly).ShouldContain(member);

    /// <summary>HX-03 is the flow's first production setter: the delivery runner, in the core, which the ban allows.</summary>
    [Fact]
    public void The_scan_sees_the_core_reach_the_flow_setter()
        => InternalMemberReferences.Of(typeof(PolicyEngine).Assembly).ShouldContain("MMLib.Alvo.AlvoFlow::Enter");

    private static (string Type, string Member) Split(string member)
    {
        var separator = member.IndexOf("::", StringComparison.Ordinal);
        return (member[..separator], member[(separator + 2)..]);
    }
}
