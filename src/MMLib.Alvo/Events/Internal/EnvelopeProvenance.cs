namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// What an event envelope can answer after the commit, and where the one remaining gap is tracked.
/// </summary>
/// <remarks>
/// Since #153 the envelope carries <c>tenantid</c> and <c>authclaims</c>, so after-hook conditions and templates resolve
/// <c>@tenant.id</c> and <c>@user.roles</c> as the caller held them at the event, and refuse to select a hook whose
/// envelope lacks them. What stays refused is a <em>wildcard</em> subscription: its matcher and the cross-tenant suite
/// it must pass are designed in <see cref="PatternSubscriptionsIssue"/>.
/// </remarks>
internal static class EnvelopeProvenance
{
    /// <summary>The issue that owns pattern subscriptions scoped by <c>tenantid</c> (N-2 of spec 2026-10-14).</summary>
    internal const string PatternSubscriptionsIssue = "#372";
}
