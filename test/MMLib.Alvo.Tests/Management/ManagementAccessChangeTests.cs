using MMLib.Alvo.Management.Internal;
using Shouldly;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// Which descriptor text counts as "changing who may reach the project" — the comparison C-1's guard turns
/// on, and the one place the two write routes could drift apart again.
/// </summary>
/// <remarks>
/// <b>An unparseable candidate is read in opposite directions by the two members, and that is the point.</b>
/// A caller's candidate that will not parse fails <em>open</em>: the validator refuses it first, so answering
/// "this is an escalation" would only send an editor to find an administrator for a descriptor no
/// administrator could apply either. A <em>stored</em> candidate that will not parse fails <em>closed</em>:
/// it is an invariant of this instance rather than a caller's mistake, and the type's own remarks already
/// said so before anything passed one.
/// </remarks>
public class ManagementAccessChangeTests
{
    private const string Applied = """
        {"apiVersion":"alvo.dev/v1","name":"p","entities":{},
         "access":{"viewer":"'ops' in @user.roles","admin":"'owner' in @user.roles"}}
        """;

    [Fact]
    public void The_same_block_spelled_differently_is_not_a_change()
    {
        ManagementAccessChange.Differs(Applied, Reformatted).ShouldBeFalse();
    }

    [Fact]
    public void A_different_block_is_a_change()
    {
        ManagementAccessChange.Differs(Applied, Widened).ShouldBeTrue();
    }

    /// <summary>A candidate the validator will refuse anyway is not an escalation.</summary>
    [Fact]
    public void A_candidate_that_will_not_parse_is_not_a_change()
    {
        ManagementAccessChange.Differs(Applied, "not a descriptor").ShouldBeFalse();
    }

    /// <summary>
    /// A <b>stored</b> descriptor that will not parse fails closed, because no validator stands behind it.
    /// </summary>
    /// <remarks>
    /// A rollback's candidate is a revision this instance already appended, so "it will not parse" is a
    /// broken invariant rather than a caller's mistake — and reading it as "no access change" would hand a
    /// <c>developer</c> a restore whose block nobody could compare. Unreachable through the apply route,
    /// which is why this is defence in depth and why it is measured here rather than over HTTP.
    /// </remarks>
    [Fact]
    public void A_stored_descriptor_that_will_not_parse_is_treated_as_a_change()
    {
        ManagementAccessChange.DiffersFromStored(Applied, "not a descriptor").ShouldBeTrue();
    }

    [Fact]
    public void A_stored_descriptor_with_the_same_block_is_not_a_change()
    {
        ManagementAccessChange.DiffersFromStored(Applied, Reformatted).ShouldBeFalse();
    }

    [Fact]
    public void A_stored_descriptor_with_a_different_block_is_a_change()
    {
        ManagementAccessChange.DiffersFromStored(Applied, Widened).ShouldBeTrue();
    }

    /// <summary>The same three predicates, re-emitted with different whitespace and key order.</summary>
    private const string Reformatted = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "p",
          "entities": {},
          "access": {
            "admin": "'owner' in @user.roles",
            "viewer": "'ops' in @user.roles"
          }
        }
        """;

    /// <summary>One predicate changed, which is a different set of three.</summary>
    private const string Widened = """
        {"apiVersion":"alvo.dev/v1","name":"p","entities":{},
         "access":{"viewer":"true","admin":"'owner' in @user.roles"}}
        """;
}
