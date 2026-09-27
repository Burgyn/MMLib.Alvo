using MMLib.Alvo.Admin.Components.Home;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>The Overview detects every qualified slot the build reports, and no slot the build does not.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>DeclaredSlots</c> restates the core's "is this key declared" predicates, because they are internal to
/// <c>MMLib.Alvo</c> and the dashboard does not reference it. A top-level block it does not know still reaches the
/// Overview through the "present and not empty" fallback, but a qualified slot (<c>auth.providers</c>,
/// <c>entity.storage</c>, <c>entity.realtime</c>) has no fallback: one added to <c>UnhonouredSubsystems</c> without a
/// reader there would be served and never drawn, which is the silence §8d item 27 closed.
/// </para>
/// <para>
/// This suite is the one that sees both assemblies' internals, so the agreement is asserted here, over the real
/// <c>CapabilityReport</c> rather than a copy of its names.
/// </para>
/// </remarks>
public sealed class DeclaredSlotsAgreementTests
{
    [Fact]
    public void Every_qualified_slot_the_build_reports_is_one_the_overview_detects()
    {
        var reported = CapabilityReport.Project().Warned
            .Select(block => block.Block)
            .Where(block => block.Contains('.', StringComparison.Ordinal))
            .ToList();

        reported.ShouldBe(
            DeclaredSlots.Qualified,
            ignoreOrder: true,
            "a slot the build reports and the Overview cannot detect is never drawn; a slot the Overview detects "
            + "and the build no longer reports is a reader of nothing");
    }
}
