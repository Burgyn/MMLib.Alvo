using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A <c>roles</c> array a self-service body carries — a member's change (<see cref="MemberPatch"/>) or an invitation's
/// (<see cref="InvitationRequest"/>): each item a JSON string naming a role from the applied <c>assignableRoles</c>, none
/// twice; every refusal at <c>/roles/{i}</c>.
/// </summary>
/// <remarks>
/// <b>One set of rules for every role list a person sends</b>, so a fix to one cannot miss the other (the
/// <see cref="SelfServiceName"/> precedent). A refusal lists the roles that are assignable — the descriptor author's names,
/// never one the caller sent.
/// </remarks>
internal static class SelfServiceRoles
{
    /// <summary>The body member every role list is sent as.</summary>
    internal const string Member = "roles";

    /// <summary>The names <paramref name="policy"/> lets a membership carry, in ordinal order.</summary>
    /// <param name="policy">The applied self-service policy.</param>
    internal static List<string> Assignable(SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return [.. policy.AssignableRoles.Select(role => role.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The names <paramref name="roles"/> holds, each refusal added to <paramref name="violations"/>; or
    /// <see langword="null"/> when it is not an array, which the caller words.
    /// </summary>
    /// <param name="roles">The <c>roles</c> member's value.</param>
    /// <param name="assignable">The assignable names, from <see cref="Assignable"/>.</param>
    /// <param name="violations">Where every refusal is added.</param>
    internal static List<string>? Read(JsonElement roles, List<string> assignable, List<AlvoViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(assignable);
        ArgumentNullException.ThrowIfNull(violations);
        if (roles.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var (role, index) in roles.EnumerateArray().Select((role, index) => (role, index)))
        {
            if (Judge(role, names, assignable, PayloadViolations.PointerTo(Member) + "/" + index.ToString(CultureInfo.InvariantCulture)) is { } refused)
            {
                violations.Add(refused);
            }
        }

        return names;
    }

    private static AlvoViolation? Judge(JsonElement role, List<string> names, List<string> assignable, string pointer)
    {
        var name = role.ValueKind == JsonValueKind.String ? SelfServiceBody.TextOf(role) : null;
        AlvoViolation? refused = name switch
        {
            null => NotARole(pointer),
            _ when names.Contains(name, StringComparer.Ordinal) => Twice(pointer),
            _ when !assignable.Contains(name, StringComparer.Ordinal) => NotAssignable(pointer, assignable),
            _ => null,
        };
        if (refused is null)
        {
            names.Add(name!);
        }

        return refused;
    }

    private static AlvoViolation NotARole(string pointer) => new(
        pointer,
        "invalid-value",
        "A role is a JSON string naming a tenant role.",
        "Send each role as a quoted name, without control characters.");

    private static AlvoViolation Twice(string pointer) => new(
        pointer,
        "invalid-value",
        "A role is named twice.",
        "Send each role once.");

    private static AlvoViolation NotAssignable(string pointer, List<string> assignable) => new(
        pointer,
        "enum-value",
        $"A role is not one a membership can carry; tenancy.selfService.assignableRoles lists {Listed(assignable)}.",
        $"Use only roles from tenancy.selfService.assignableRoles: {Listed(assignable)}.");

    private static string Listed(List<string> assignable) => assignable.Count == 0 ? "none" : string.Join(", ", assignable);
}
