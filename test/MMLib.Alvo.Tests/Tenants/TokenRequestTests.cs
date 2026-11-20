using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tenants.SelfService;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// A person's new app token (S5 plan D7, D12, D13): a name, a kind, scopes of real entities and an expiry within
/// <c>maxLifetimeDays</c>; a service token's tenant and roles from <c>assignableRoles</c>, a personal token's no roles —
/// each refused at its pointer, never clamped, and never echoing what was sent.
/// </summary>
public sealed class TokenRequestTests
{
    private const string Tenant = "4f0c8d5e-7a2b-4c1d-9e3f-6a5b4c3d2e1f";

    private const string Rest = "\"scopes\":[\"jobs:read\"],\"expiresAt\":\"2026-11-20T09:00:00Z\"";

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly TokenRules _rules = new(
        new AppTokenPolicy(true, true, 30),
        new SelfServicePolicy(
            true,
            new HashSet<Role> { Role.Application("dispatcher"), Role.Application("technician") },
            null,
            new HashSet<Role> { Role.Application("dispatcher") },
            5,
            Invitations: false),
        new HashSet<string> { "regions", "jobs", "notes" },
        _now);

    private static readonly string _longName = new('n', 101);

    public static TheoryData<string, string> Refused => new()
    {
        { $$"""{"kind":"personal",{{Rest}}}""", "/name" },
        { $$"""{"name":"","kind":"personal",{{Rest}}}""", "/name" },
        { $$"""{"name":"   ","kind":"personal",{{Rest}}}""", "/name" },
        { $$"""{"name":"{{_longName}}","kind":"personal",{{Rest}}}""", "/name" },
        { $$"""{"name":"a\u0001b","kind":"personal",{{Rest}}}""", "/name" },
        { $$"""{"name":"ci",{{Rest}}}""", "/kind" },
        { $$"""{"name":"ci","kind":"owner",{{Rest}}}""", "/kind" },
        { $$"""{"name":"ci","kind":7,{{Rest}}}""", "/kind" },
        { $$"""{"name":"ci","kind":"personal","tenantId":"x",{{Rest}}}""", "/tenantId" },
        { $$"""{"name":"ci","kind":"personal","tenantId":7,{{Rest}}}""", "/tenantId" },
        { $$"""{"name":"ci","kind":"service","roles":["dispatcher"],{{Rest}}}""", "/tenantId" },
        { $$"""{"name":"ci","kind":"personal","roles":["dispatcher"],{{Rest}}}""", "/roles" },
        { $$"""{"name":"ci","kind":"personal","roles":[],{{Rest}}}""", "/roles" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}",{{Rest}}}""", "/roles" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}","roles":[],{{Rest}}}""", "/roles" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}","roles":"dispatcher",{{Rest}}}""", "/roles" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}","roles":["dispatcher","dispatcher"],{{Rest}}}""", "/roles/1" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}","roles":["admin"],{{Rest}}}""", "/roles/0" },
        { $$"""{"name":"ci","kind":"service","tenantId":"{{Tenant}}","roles":["auditor"],{{Rest}}}""", "/roles/0" },
        { """{"name":"ci","kind":"personal","expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes" },
        { """{"name":"ci","kind":"personal","scopes":[],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes" },
        { """{"name":"ci","kind":"personal","scopes":"jobs:read","expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes" },
        { """{"name":"ci","kind":"personal","scopes":["jobs"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:READ"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":["*:read"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":["*:write"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":["widgets:read"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":[7],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/0" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read","jobs:read"],"expiresAt":"2026-11-20T09:00:00Z"}""", "/scopes/1" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"]}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-11-13T08:59:59Z"}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-11-13T09:00:00Z"}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-11-20T09:00:00"}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-12-13T09:00:01Z"}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"tomorrow"}""", "/expiresAt" },
        { """{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":1763024400}""", "/expiresAt" },
        { $$"""{"name":"ci","kind":"personal",{{Rest}},"extra":1}""", "/extra" },
    };

    [Fact]
    public void A_personal_request_needs_no_tenant_and_no_roles()
    {
        var token = Valid($$"""{"name":" ci ","kind":"personal",{{Rest}}}""");

        token.Name.ShouldBe("ci");
        token.Kind.ShouldBe(ApiKeyKind.Personal);
        token.Tenant.ShouldBeNull();
        token.Roles.ShouldBeEmpty();
        token.ScopeTexts.ShouldBe(["jobs:read"]);
        token.ExpiresAt.ShouldBe(_now.AddDays(7));
    }

    [Fact]
    public void A_service_request_carries_its_tenant_roles_and_scopes()
    {
        var token = Valid(
            $$"""{"name":"sync","kind":"service","tenantId":"{{Tenant}}","roles":["technician","dispatcher"],"scopes":["notes:write","jobs:read","regions:read"],"expiresAt":"2026-11-14T10:00:00+01:00"}""");

        token.Kind.ShouldBe(ApiKeyKind.Service);
        token.Tenant.ShouldBe(new TenantId(Guid.Parse(Tenant)));
        token.Roles.ShouldBe(["technician", "dispatcher"]);
        token.ScopeTexts.ShouldBe(["notes:write", "jobs:read", "regions:read"]);
        token.ExpiresAt.ShouldBe(_now.AddDays(1));
    }

    [Fact]
    public void A_personal_request_may_name_a_tenant_and_null_is_absent()
    {
        var token = Valid($$"""{"name":"ci","kind":"personal","tenantId":"{{Tenant}}","roles":null,{{Rest}}}""");

        token.Tenant.ShouldBe(new TenantId(Guid.Parse(Tenant)));
        token.Roles.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_bound_is_refused_at_its_pointer(string json, string at)
    {
        var (value, violations) = Validate(json);

        value.ShouldBeNull();
        violations.Select(violation => violation.Pointer).ShouldBe([at]);
    }

    [Fact]
    public void An_unknown_entity_is_refused_with_the_descriptor_s_entities()
    {
        var (_, violations) = Validate("""{"name":"ci","kind":"personal","scopes":["secretwidgets:read"],"expiresAt":"2026-11-20T09:00:00Z"}""");

        var refusal = violations.ShouldHaveSingleItem();
        refusal.Message.ShouldContain("jobs, notes, regions");
        refusal.FixSuggestion.ShouldNotBeNull().ShouldContain("jobs, notes, regions");
        refusal.Message.ShouldNotContain("secretwidgets");
        refusal.FixSuggestion.ShouldNotContain("secretwidgets");
    }

    [Fact]
    public void Exactly_max_lifetime_is_accepted()
    {
        Valid("""{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-12-13T09:00:00Z"}""")
            .ExpiresAt.ShouldBe(_now.AddDays(30));
        Valid("""{"name":"ci","kind":"personal","scopes":["jobs:read"],"expiresAt":"2026-11-13T09:00:01Z"}""")
            .ExpiresAt.ShouldBe(_now.AddSeconds(1));
    }

    [Fact]
    public void Every_violation_of_one_body_is_reported_together()
    {
        Pointers("""{"kind":"owner","scopes":["jobs"],"expiresAt":"x"}""").ShouldBe(["/name", "/kind", "/scopes/0", "/expiresAt"]);
    }

    [Fact]
    public void No_violation_echoes_what_was_sent()
    {
        const string Body = """{"name":"{}","kind":"secretkind1","tenantId":"secret2","roles":"secret3","scopes":["secret4","secret5:read"],"expiresAt":"secret6","secret7":1}""";

        var (_, violations) = Validate(Body.Replace("\"{}\"", "\"a\\u0001secret0\"", StringComparison.Ordinal));

        violations.Count.ShouldBe(7);
        foreach (var text in violations.SelectMany(violation => new[] { violation.Message, violation.FixSuggestion ?? string.Empty }))
        {
            text.ShouldNotContain("secret");
        }
    }

    private static TokenRequest Valid(string json)
    {
        var (value, violations) = Validate(json);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(violation => $"{violation.Pointer}: {violation.Message}")));
        return value.ShouldNotBeNull();
    }

    private static string[] Pointers(string json) => [.. Validate(json).Violations.Select(violation => violation.Pointer)];

    private static (TokenRequest? Value, IReadOnlyList<AlvoViolation> Violations) Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TokenRequest.Validate(document.RootElement, _rules);
    }
}
