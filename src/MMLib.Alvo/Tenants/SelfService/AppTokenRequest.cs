using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>What a token request is judged against: the applied descriptor's token and self-service policy, its entities, and now.</summary>
/// <param name="Tokens">The applied <c>auth.accessTokens</c>, for the longest expiry.</param>
/// <param name="SelfService">The applied self-service policy, whose <c>assignableRoles</c> a service token's roles come from.</param>
/// <param name="Entities">The applied schema's entity names, which every scope names one of.</param>
/// <param name="Now">The instant the request acts at.</param>
internal sealed record TokenRules(
    AppTokenPolicy Tokens, SelfServicePolicy SelfService, IReadOnlySet<string> Entities, DateTimeOffset Now);

/// <summary>
/// The body of <c>POST {prefix}/tokens</c> (S5 plan D7, D12, D13): <c>name</c>, <c>kind</c>, <c>scopes</c> and
/// <c>expiresAt</c>, and for a service token <c>tenantId</c> and <c>roles</c>, for a personal one optionally
/// <c>tenantId</c> — nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refused, never clamped</b>: the name under <see cref="SelfServiceName"/>'s rules; a service token's roles from the
/// applied <c>assignableRoles</c>, at least one, none twice (<see cref="SelfServiceRoles"/>) — and no roles at all on a
/// personal token, whose roles are its person's, read on every request; scopes of the form
/// <c>&lt;entity&gt;:&lt;read|write&gt;</c> naming an entity the applied schema declares, at least one, none twice, and
/// never the wildcard — a <c>*</c> would grant the entities added after the mint; <c>expiresAt</c> an RFC 3339 instant with an
/// offset, after now and at most <c>maxLifetimeDays</c> days away.
/// </para>
/// <para><b>Every refusal is a fixed sentence</b> at the member's pointer; none echoes a value that was sent.</para>
/// </remarks>
/// <param name="Name">The token's trimmed name.</param>
/// <param name="Kind"><see cref="ApiKeyKind.Personal"/> or <see cref="ApiKeyKind.Service"/>.</param>
/// <param name="Tenant">The tenant it acts in; <see langword="null"/> only for a tenantless personal token.</param>
/// <param name="Roles">A service token's tenant roles, in the order sent; empty for a personal one.</param>
/// <param name="Scopes">What it may read and write.</param>
/// <param name="ExpiresAt">When it expires.</param>
internal sealed record AppTokenRequest(
    string Name,
    ApiKeyKind Kind,
    TenantId? Tenant,
    IReadOnlyList<string> Roles,
    IReadOnlyList<ApiKeyScope> Scopes,
    DateTimeOffset ExpiresAt)
{
    private const string KindMember = "kind";

    private const string TenantMember = "tenantId";

    private const string ScopesMember = "scopes";

    private const string ExpiresAtMember = "expiresAt";

    private const string PersonalKind = "personal";

    private const string ServiceKind = "service";

    /// <summary>How far ahead the <c>expiresAt</c> hint's example lies, when <c>maxLifetimeDays</c> allows that far.</summary>
    private const int ExampleDays = 30;

    private static readonly SelfServiceName _name = new("token name");

    /// <summary>Gets <see cref="Scopes"/> in the form a stored token keeps them, <c>&lt;entity&gt;:&lt;read|write&gt;</c>.</summary>
    internal IReadOnlyList<string> ScopeTexts =>
        [.. Scopes.Select(scope => scope.Entity + ":" + (scope.Access == ScopeAccess.Read ? "read" : "write"))];

    /// <summary>The token the request asks for, or every reason it asks for none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="rules">What the request is judged against.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(AppTokenRequest? Value, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, TokenRules rules, CancellationToken ct) =>
        SelfServiceBody.ReadAsync(request, options, root => Validate(root, rules), ct);

    /// <summary>The token <paramref name="root"/> asks for, or every reason it asks for none.</summary>
    /// <param name="root">The body's top-level object.</param>
    /// <param name="rules">What the request is judged against.</param>
    internal static (AppTokenRequest? Value, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root, TokenRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var violations = SelfServiceBody.Unknown(
            root, "name", KindMember, TenantMember, SelfServiceRoles.Member, ScopesMember, ExpiresAtMember);
        var name = _name.ReadFrom(root, violations);
        var kind = ReadKind(root, violations);
        var tenant = ReadTenant(root, kind, violations);
        var roles = ReadRoles(root, kind, rules, violations);
        var scopes = ReadScopes(root, rules, violations);
        var expiresAt = ReadExpiresAt(root, rules, violations);
        return violations.Count == 0
            ? (new AppTokenRequest(name!, kind!.Value, tenant, roles, scopes, expiresAt), [])
            : (null, violations);
    }

    private static ApiKeyKind? ReadKind(JsonElement root, List<AlvoViolation> violations)
    {
        var sent = Present(root, KindMember, out var value);
        var kind = sent && value.ValueKind == JsonValueKind.String ? SelfServiceBody.TextOf(value) : null;
        if (kind is PersonalKind or ServiceKind)
        {
            return kind == PersonalKind ? ApiKeyKind.Personal : ApiKeyKind.Service;
        }

        violations.Add(Refusal(
            KindMember,
            sent ? "enum-value" : "required",
            "kind is \"personal\" or \"service\".",
            "Send \"kind\": \"personal\" for a token that acts as you, or \"service\" for one that acts as a tenant."));
        return null;
    }

    private static TenantId? ReadTenant(JsonElement root, ApiKeyKind? kind, List<AlvoViolation> violations)
    {
        if (!Present(root, TenantMember, out var value))
        {
            if (kind == ApiKeyKind.Service)
            {
                violations.Add(Refusal(
                    TenantMember, "required", "A service token acts in one tenant, which tenantId names.", "Send \"tenantId\": the id of a tenant you own."));
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String && Guid.TryParseExact(SelfServiceBody.TextOf(value), "D", out var id))
        {
            return new TenantId(id);
        }

        violations.Add(Refusal(
            TenantMember, "invalid-value", "tenantId is a tenant's id: a UUID in its 8-4-4-4-12 form.", "Send a tenant id as GET /tenants lists it."));
        return null;
    }

    private static List<string> ReadRoles(JsonElement root, ApiKeyKind? kind, TokenRules rules, List<AlvoViolation> violations)
    {
        var present = Present(root, SelfServiceRoles.Member, out var roles);
        if (kind == ApiKeyKind.Personal && present)
        {
            violations.Add(Refusal(
                SelfServiceRoles.Member,
                "invalid-value",
                "A personal token has no roles of its own: it holds its person's tenant roles, read on every request.",
                "Leave roles out of a personal token."));
        }

        if (kind != ApiKeyKind.Service)
        {
            return [];
        }

        return present ? ReadServiceRoles(roles, rules, violations) : MissingRoles(violations);
    }

    private static List<string> ReadServiceRoles(JsonElement roles, TokenRules rules, List<AlvoViolation> violations)
    {
        var names = SelfServiceRoles.Read(roles, SelfServiceRoles.Assignable(rules.SelfService), violations);
        if (names is null || roles.GetArrayLength() == 0)
        {
            violations.Add(Refusal(
                SelfServiceRoles.Member,
                "invalid-value",
                "roles is a JSON array of at least one tenant role.",
                "Send \"roles\": [ \"…\" ], each a role you hold in the tenant."));
        }

        return names ?? [];
    }

    private static List<string> MissingRoles(List<AlvoViolation> violations)
    {
        violations.Add(Refusal(
            SelfServiceRoles.Member,
            "required",
            "A service token names the tenant roles it acts with.",
            "Send \"roles\": [ \"…\" ], each a role you hold in the tenant."));
        return [];
    }

    private static List<ApiKeyScope> ReadScopes(JsonElement root, TokenRules rules, List<AlvoViolation> violations)
    {
        var scopes = new List<ApiKeyScope>();
        var sent = Present(root, ScopesMember, out var value);
        if (!sent || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            violations.Add(Refusal(
                ScopesMember,
                sent ? "invalid-value" : "required",
                "scopes is a JSON array of at least one scope, e.g. \"jobs:read\".",
                "Send \"scopes\": [ \"<entity>:read\" ] or \"<entity>:write\", one per entity and access."));
            return scopes;
        }

        var texts = new List<string>();
        foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
        {
            var pointer = PayloadViolations.PointerTo(ScopesMember) + "/" + index.ToString(CultureInfo.InvariantCulture);
            if (JudgeScope(item, texts, rules, pointer, scopes) is { } refused)
            {
                violations.Add(refused);
            }
        }

        return scopes;
    }

    private static AlvoViolation? JudgeScope(
        JsonElement item, List<string> texts, TokenRules rules, string pointer, List<ApiKeyScope> scopes)
    {
        var text = item.ValueKind == JsonValueKind.String ? SelfServiceBody.TextOf(item) : null;
        if (text is null || !ApiKeyScope.TryParse(text, out var scope))
        {
            return At(pointer, "invalid-value", "A scope is \"<entity>:read\" or \"<entity>:write\".", "Send e.g. \"jobs:read\", in lower case.");
        }

        if (RefusalOf(scope, text, texts, rules, pointer) is { } refused)
        {
            return refused;
        }

        texts.Add(text);
        scopes.Add(scope);
        return null;
    }

    private static AlvoViolation? RefusalOf(ApiKeyScope scope, string text, List<string> texts, TokenRules rules, string pointer) => scope.Entity switch
    {
        "*" => At(pointer, "invalid-value", "A scope names one entity; the wildcard is not accepted.", "Name each entity the token needs."),
        _ when !rules.Entities.Contains(scope.Entity) => At(
            pointer,
            "enum-value",
            "A scope names an entity the applied descriptor does not declare; its entities are " + Listed(rules) + ".",
            "Use only entities the applied descriptor declares: " + Listed(rules) + "."),
        _ when texts.Contains(text, StringComparer.Ordinal) => At(pointer, "invalid-value", "A scope is named twice.", "Send each scope once."),
        _ => null,
    };

    private static string Listed(TokenRules rules) =>
        rules.Entities.Count == 0 ? "none" : string.Join(", ", rules.Entities.Order(StringComparer.Ordinal));

    private static DateTimeOffset ReadExpiresAt(JsonElement root, TokenRules rules, List<AlvoViolation> violations)
    {
        var sent = Present(root, ExpiresAtMember, out var value);
        var instant = sent && value.ValueKind == JsonValueKind.String ? SelfServiceBody.InstantOf(SelfServiceBody.TextOf(value)) : null;
        var days = rules.Tokens.MaxLifetimeDays;
        if (instant is { } expires && expires > rules.Now && expires <= rules.Now + TimeSpan.FromDays(days))
        {
            return expires;
        }

        violations.Add(Refusal(
            ExpiresAtMember,
            sent ? "invalid-value" : "required",
            string.Create(
                CultureInfo.InvariantCulture,
                $"expiresAt is an RFC 3339 instant with an offset, in the future and at most {days} days away."),
            $"Send e.g. \"{SelfServiceBody.ExampleInstant(rules.Now + TimeSpan.FromDays(Math.Min(days, ExampleDays)))}\"; "
                + "a token always expires."));
        return rules.Now;
    }

    private static bool Present(JsonElement root, string member, out JsonElement value) =>
        root.TryGetProperty(member, out value) && value.ValueKind != JsonValueKind.Null;

    private static AlvoViolation At(string pointer, string code, string message, string hint) => new(pointer, code, message, hint);

    private static AlvoViolation Refusal(string member, string code, string message, string hint) =>
        new(PayloadViolations.PointerTo(member), code, message, hint);
}
