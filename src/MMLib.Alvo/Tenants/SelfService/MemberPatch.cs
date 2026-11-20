using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The body of <c>PATCH {prefix}/tenants/{id}/members/{userId}</c>: <c>{ "roles"?: string[], "isOwner"?: bool }</c> — at
/// least one, nothing else; a part left out is left as it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roles are judged here, against the applied <c>assignableRoles</c></b> (spec §6.3, §14 AC4): each one a declared tenant
/// role, none twice. So <c>admin</c>, <c>owner</c> or any name the descriptor does not list as assignable is refused at
/// <c>/roles/{i}</c> before the registry is touched, and the refusal lists the roles that are assignable — the descriptor
/// author's names, never one the caller sent.
/// </para>
/// <para>The registry stores roles verbatim; every read intersects them with the descriptor again (S3 plan D5).</para>
/// </remarks>
/// <param name="Roles">The member's new roles, or <see langword="null"/> to keep them.</param>
/// <param name="IsOwner">The member's new ownership, or <see langword="null"/> to keep it.</param>
internal sealed record MemberPatch(IReadOnlyList<string>? Roles, bool? IsOwner)
{
    private const string RolesMember = SelfServiceRoles.Member;

    private const string IsOwnerMember = "isOwner";

    /// <summary>The change the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="policy">The applied self-service policy, whose <c>assignableRoles</c> the roles must come from.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(MemberPatch? Value, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, SelfServicePolicy policy, CancellationToken ct)
    {
        var assignable = SelfServiceRoles.Assignable(policy);
        return SelfServiceBody.ReadAsync(request, options, root => Validate(root, assignable), ct);
    }

    private static (MemberPatch? Value, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root, List<string> assignable)
    {
        var violations = SelfServiceBody.Unknown(root, RolesMember, IsOwnerMember);
        var hasRoles = root.TryGetProperty(RolesMember, out var roles);
        var hasOwner = root.TryGetProperty(IsOwnerMember, out var owner);
        if (!hasRoles && !hasOwner)
        {
            violations.Add(Nothing());
        }

        var names = hasRoles ? ReadRoles(roles, assignable, violations) : null;
        bool? isOwner = hasOwner ? ReadOwner(owner, violations) : null;
        return violations.Count == 0 ? (new MemberPatch(names, isOwner), []) : (null, violations);
    }

    private static List<string>? ReadRoles(JsonElement roles, List<string> assignable, List<AlvoViolation> violations)
    {
        var names = SelfServiceRoles.Read(roles, assignable, violations);
        if (names is null)
        {
            violations.Add(NotRoles());
        }

        return names;
    }

    private static bool? ReadOwner(JsonElement owner, List<AlvoViolation> violations)
    {
        if (owner.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return owner.GetBoolean();
        }

        violations.Add(new AlvoViolation(
            PayloadViolations.PointerTo(IsOwnerMember),
            "invalid-value",
            "isOwner is true or false.",
            "Send \"isOwner\": true to make the member an owner, or false to take ownership away."));
        return null;
    }

    private static AlvoViolation Nothing() => new(
        string.Empty,
        "required",
        "A membership change names roles, isOwner, or both.",
        "Send { \"roles\": [ … ] }, { \"isOwner\": true }, or both.");

    private static AlvoViolation NotRoles() => new(
        PayloadViolations.PointerTo(RolesMember),
        "invalid-value",
        "roles is a JSON array of role names.",
        "Send \"roles\": [ \"…\" ]; an empty array takes every tenant role away.");
}
