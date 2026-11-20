using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Tenants.SelfService;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The body of <c>GET {prefix}/user</c> and of a granted <c>PATCH</c>: the account as its person sees it.</summary>
/// <remarks>
/// Member names are fixed with <see cref="JsonPropertyNameAttribute"/> and written with this type's own serializer options,
/// so a host's naming policy cannot rename a member a client reads (the <see cref="TokenResponse"/> precedent).
/// </remarks>
/// <param name="Id">The account's id — the <c>@user.id</c> its rows are written as.</param>
/// <param name="Email">The email the last sign-in asserted.</param>
/// <param name="EmailTrusted">Whether that email is trusted (spec §5.3).</param>
/// <param name="Name">The display name.</param>
/// <param name="AvatarUrl">The avatar URL the last sign-in asserted.</param>
/// <param name="Providers">The providers the account signs in with.</param>
/// <param name="Memberships">
/// The tenants the person belongs to that the applied descriptor honours, with their effective roles (S3 plan D5); empty
/// without <c>tenancy.selfService</c>.
/// </param>
internal sealed record UserResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("emailTrusted")] bool EmailTrusted,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl,
    [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers,
    [property: JsonPropertyName("memberships")] IReadOnlyList<MembershipResponse> Memberships)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    /// <summary>The person's view of <paramref name="account"/> and its <paramref name="memberships"/>.</summary>
    /// <param name="account">The account as the store reads it.</param>
    /// <param name="memberships">The memberships the applied descriptor honours.</param>
    internal static UserResponse From(AccountView account, IReadOnlyList<MembershipResponse> memberships)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(memberships);
        return new(
            account.Id.Value, account.Email, account.EmailTrusted, account.DisplayName, account.AvatarUrl, account.Providers, memberships);
    }

    /// <summary>The 200 carrying this body.</summary>
    internal IResult Ok() => Results.Json(this, _json, contentType: "application/json", statusCode: StatusCodes.Status200OK);
}
