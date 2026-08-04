namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <b>The one authority on what provenance an event envelope carries, and therefore on which caller
/// references a post-commit path can answer.</b> Every refusal of <c>@tenant.id</c> or <c>@user.roles</c> —
/// in a template placeholder and in an after-hook CEL condition alike — composes its message from here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the words live in one place.</b> The two halves refuse the same two names for the same two
/// reasons, and they were written twice: <see cref="TemplatePlaceholder"/> had the template half right while
/// the CEL half admitted both names silently. Two copies of "the envelope carries no tenant" is how one side
/// comes to be relaxed without the other, which is exactly the defect
/// <see cref="Descriptor.Internal.UnhonouredFeatures"/> was tabulated to prevent.
/// </para>
/// <para>
/// <b>The rule the two halves now share: resolve what the envelope can answer, refuse what it cannot.</b>
/// <see cref="AlvoEvent.AuthId"/> exists, so <c>@user.id</c> is answered from it — in a template and in a
/// condition. There is no tenant attribute and no role list on the envelope at all, so <c>@tenant.id</c> and
/// <c>@user.roles</c> are refused <em>by name</em> rather than resolved from something adjacent: they are
/// real Alvo CEL context references, so "unknown root" would misdescribe why they fail. Giving
/// <c>@tenant.id</c> a real answer is a public-API and wire-format change, tracked as <b>#153</b>.
/// </para>
/// </remarks>
internal static class EnvelopeProvenance
{
    /// <summary>Why <c>@tenant.id</c> cannot be answered after the commit.</summary>
    internal const string NoTenant =
        "the event envelope carries no tenant attribute, so nothing after the commit knows which tenant the "
        + "caller was in";

    /// <summary>Why <c>@user.roles</c> cannot be answered after the commit.</summary>
    internal const string NoRoles =
        "an event envelope carries authentication and never authorization, so nothing after the commit knows "
        + "which roles the caller held";

    /// <summary>
    /// What to use instead of <c>@tenant.id</c>: the row's own tenant column, which answers a
    /// <em>different</em> question and the only one the envelope can answer.
    /// </summary>
    /// <param name="rowReference">How the surrounding language spells a reference to the row's tenant column.</param>
    internal static string InsteadOfTenant(string rowReference) =>
        $"On a tenant-scoped entity, use the row's own '{rowReference}' instead — it answers which tenant the "
        + "row belongs to, which is a different question and the only one the envelope can answer. A real "
        + "'@tenant.id' is a public-API and wire-format change, tracked in issue #153.";

    /// <summary>What to use instead of <c>@user.roles</c>, and where the capability is tracked.</summary>
    internal static string InsteadOfRoles { get; } =
        "Test something the envelope carries instead — '@user.id' is the credential that acted — or move the "
        + "role check into a rule, which runs with the caller's own context. Identity claims an event does not "
        + "yet carry are tracked in issues #146 and #37.";
}
