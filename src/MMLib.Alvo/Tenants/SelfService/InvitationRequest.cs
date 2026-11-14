using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The body of <c>POST {prefix}/tenants/{id}/invitations</c> (S4 plan D9): <c>roles</c> (required), and optionally
/// <c>isOwner</c>, <c>email</c>, <c>maxUses</c> and <c>expiresAt</c> — nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's rules, refused and never clamped</b>: each role from the applied <c>assignableRoles</c>, none twice
/// (<see cref="SelfServiceRoles"/>, a member change's own rules); <c>maxUses</c> 1–<see cref="MaxUsesLimit"/>, default 1;
/// <c>expiresAt</c> an RFC 3339 instant with an offset, after now and at most <see cref="MaxLifetime"/> away, default
/// <see cref="DefaultLifetime"/>. An invitation that makes an owner is single-use and lives at most
/// <see cref="OwnerLifetime"/> — the spec's guard on handing out ownership (§6.4).
/// </para>
/// <para>
/// <b><c>email</c> binds the invitation to one trusted address</b>: trimmed, at most <see cref="MaxEmailLength"/>
/// characters, exactly one <c>@</c> with something on each side, no whitespace or control character, stored lower-case.
/// </para>
/// <para><b>Every refusal is a fixed sentence</b> at the member's pointer; none echoes a value that was sent.</para>
/// </remarks>
/// <param name="Roles">The tenant roles an accepted membership starts with.</param>
/// <param name="IsOwner">Whether an accepted membership is an owner's.</param>
/// <param name="Email">The trusted email the invitation is bound to, lower-case, or <see langword="null"/>.</param>
/// <param name="MaxUses">How many people may accept it.</param>
/// <param name="ExpiresAt">The instant from which it admits nobody.</param>
internal sealed record InvitationRequest(
    IReadOnlyList<string> Roles, bool IsOwner, string? Email, int MaxUses, DateTimeOffset ExpiresAt)
{
    /// <summary>The most people one invitation may admit (the builder's bound; the spec gives only the default).</summary>
    internal const int MaxUsesLimit = 100;

    /// <summary>The longest bound email, in UTF-16 code units (RFC 5321's path limit less its angle brackets).</summary>
    internal const int MaxEmailLength = 254;

    private const string RolesHint = "Send \"roles\": [ \"…\" ]; an empty array invites someone with no tenant role.";

    private const string IsOwnerMember = "isOwner";

    private const string EmailMember = "email";

    private const string MaxUsesMember = "maxUses";

    private const string ExpiresAtMember = "expiresAt";

    /// <summary>Gets how long an invitation lives when <c>expiresAt</c> is not sent.</summary>
    internal static TimeSpan DefaultLifetime { get; } = TimeSpan.FromDays(7);

    /// <summary>Gets the longest an invitation may live.</summary>
    internal static TimeSpan MaxLifetime { get; } = TimeSpan.FromDays(30);

    /// <summary>Gets the longest an invitation that makes an owner may live.</summary>
    internal static TimeSpan OwnerLifetime { get; } = TimeSpan.FromDays(7);

    /// <summary>The invitation the request asks for, or every reason it asks for none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="policy">The applied self-service policy, whose <c>assignableRoles</c> the roles must come from.</param>
    /// <param name="now">The instant the request acts at.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(InvitationRequest? Value, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, SelfServicePolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var assignable = SelfServiceRoles.Assignable(policy);
        return SelfServiceBody.ReadAsync(request, options, root => Validate(root, assignable, now), ct);
    }

    /// <summary>The invitation <paramref name="root"/> asks for, or every reason it asks for none.</summary>
    /// <param name="root">The body's top-level object.</param>
    /// <param name="policy">The applied self-service policy.</param>
    /// <param name="now">The instant the request acts at.</param>
    internal static (InvitationRequest? Value, IReadOnlyList<AlvoViolation> Violations) Validate(
        JsonElement root, SelfServicePolicy policy, DateTimeOffset now) =>
        Validate(root, SelfServiceRoles.Assignable(policy), now);

    private static (InvitationRequest? Value, IReadOnlyList<AlvoViolation> Violations) Validate(
        JsonElement root, List<string> assignable, DateTimeOffset now)
    {
        var violations = SelfServiceBody.Unknown(
            root, SelfServiceRoles.Member, IsOwnerMember, EmailMember, MaxUsesMember, ExpiresAtMember);
        var roles = ReadRoles(root, assignable, violations);
        var isOwner = ReadOwner(root, violations);
        var email = ReadEmail(root, violations);
        var maxUses = ReadMaxUses(root, isOwner, violations);
        var expiresAt = ReadExpiresAt(root, isOwner, now, violations);
        return violations.Count == 0 ? (new InvitationRequest(roles!, isOwner, email, maxUses, expiresAt), []) : (null, violations);
    }

    private static List<string>? ReadRoles(JsonElement root, List<string> assignable, List<AlvoViolation> violations)
    {
        if (!root.TryGetProperty(SelfServiceRoles.Member, out var roles))
        {
            violations.Add(Refusal(SelfServiceRoles.Member, "required", "An invitation names the roles it grants.", RolesHint));
            return null;
        }

        var names = SelfServiceRoles.Read(roles, assignable, violations);
        if (names is null)
        {
            violations.Add(Refusal(SelfServiceRoles.Member, "invalid-value", "roles is a JSON array of role names.", RolesHint));
        }

        return names;
    }

    private static bool ReadOwner(JsonElement root, List<AlvoViolation> violations)
    {
        if (!root.TryGetProperty(IsOwnerMember, out var owner) || owner.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        if (owner.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        violations.Add(Refusal(
            IsOwnerMember, "invalid-value", "isOwner is true or false.", "Send \"isOwner\": true to invite an owner, or leave it out."));
        return false;
    }

    private static string? ReadEmail(JsonElement root, List<AlvoViolation> violations)
    {
        if (!root.TryGetProperty(EmailMember, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var email = value.ValueKind == JsonValueKind.String ? SelfServiceBody.TextOf(value)?.Trim() : null;
        if (email is not null && IsAddress(email))
        {
            return email.ToLowerInvariant();
        }

        violations.Add(Refusal(
            EmailMember,
            "invalid-value",
            string.Create(
                CultureInfo.InvariantCulture,
                $"email is one address of at most {MaxEmailLength} characters: one @ with text on each side, no whitespace."),
            "Send the address the invitee signs in with, e.g. \"ada@example.com\", or leave email out to invite anyone."));
        return null;
    }

    private static bool IsAddress(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return email.Length <= MaxEmailLength
            && at > 0
            && at == email.LastIndexOf('@')
            && at < email.Length - 1
            && !email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }

    private static int ReadMaxUses(JsonElement root, bool isOwner, List<AlvoViolation> violations)
    {
        if (!root.TryGetProperty(MaxUsesMember, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 1;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var uses) || uses is < 1 or > MaxUsesLimit)
        {
            violations.Add(Refusal(
                MaxUsesMember,
                "invalid-value",
                string.Create(CultureInfo.InvariantCulture, $"maxUses is a whole number from 1 to {MaxUsesLimit}."),
                "Send how many people may accept the invitation, or leave maxUses out for one."));
            return 1;
        }

        if (isOwner && uses != 1)
        {
            violations.Add(Refusal(
                MaxUsesMember, "invalid-value", "An invitation that makes an owner admits one person.", "Send \"maxUses\": 1, or leave it out."));
        }

        return uses;
    }

    private static DateTimeOffset ReadExpiresAt(JsonElement root, bool isOwner, DateTimeOffset now, List<AlvoViolation> violations)
    {
        if (!root.TryGetProperty(ExpiresAtMember, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return now + DefaultLifetime;
        }

        var instant = value.ValueKind == JsonValueKind.String ? InstantOf(SelfServiceBody.TextOf(value)) : null;
        var latest = now + (isOwner ? OwnerLifetime : MaxLifetime);
        if (instant is { } expires && expires > now && expires <= latest)
        {
            return expires;
        }

        violations.Add(Refusal(
            ExpiresAtMember,
            "invalid-value",
            isOwner
                ? "expiresAt of an invitation that makes an owner is an RFC 3339 instant with an offset, in the future and at most 7 days away."
                : "expiresAt is an RFC 3339 instant with an offset, in the future and at most 30 days away.",
            "Send e.g. \"2026-12-01T09:00:00Z\", or leave expiresAt out for 7 days from now."));
        return now;
    }

    private static DateTimeOffset? InstantOf(string? text) =>
        text is not null
        && HasOffset(text)
        && DateTimeOffset.TryParseExact(
            text, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : null;

    private static bool HasOffset(string text) =>
        text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');

    private static AlvoViolation Refusal(string member, string code, string message, string hint) =>
        new(PayloadViolations.PointerTo(member), code, message, hint);
}
