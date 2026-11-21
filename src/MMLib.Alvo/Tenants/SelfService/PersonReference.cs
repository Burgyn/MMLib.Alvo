using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A person one self-service resource names — its creator — as every <c>/auth/v1</c> answer names one (#542 item 1):
/// <c>{ "id", "name" }</c>, under the one key <c>createdBy</c>.
/// </summary>
/// <remarks>
/// <c>name</c> is the display name <c>/auth/v1/user</c> and the member list answer (OpenID Connect Core §5.1's <c>name</c>,
/// which sign-in fills it from), as it reads now; <see langword="null"/> when the person has none, never omitted. Member
/// names are fixed with <see cref="JsonPropertyNameAttribute"/>, so a host's naming policy cannot rename them.
/// </remarks>
/// <param name="Id">The person's account id.</param>
/// <param name="Name">The person's display name, or <see langword="null"/>.</param>
internal sealed record PersonReference(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>
/// A person named to someone who has not signed in — the anonymous invitation preview's creator: the display name alone,
/// under the same key and path as <see cref="PersonReference"/>, and never the account id (#542 item 1).
/// </summary>
/// <remarks>
/// The id is omitted, not <see langword="null"/>: a <see langword="null"/> would read as "this person has no id", and the
/// preview never disclosed one (S4 plan D8). A separate type, so the OpenAPI document publishes two shapes rather than one
/// with an optional id.
/// </remarks>
/// <param name="Name">The person's display name, or <see langword="null"/>.</param>
internal sealed record PersonName([property: JsonPropertyName("name")] string? Name);
