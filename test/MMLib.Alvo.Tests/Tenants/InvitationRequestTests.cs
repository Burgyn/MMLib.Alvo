using MMLib.Alvo.Api;
using MMLib.Alvo.Tenants.SelfService;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// An owner's new invitation (S4 plan D9): <c>roles</c> from <c>assignableRoles</c>, <c>maxUses</c> 1–100, <c>expiresAt</c>
/// in the future and at most 30 days away, an <c>email</c> that is one address — an owner-making one single-use and at
/// most 7 days — each refused at its pointer, never clamped, and never echoing what was sent.
/// </summary>
public sealed class InvitationRequestTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly SelfServicePolicy _policy = new(
        true,
        new HashSet<Role> { Role.Application("dispatcher"), Role.Application("technician") },
        null,
        new HashSet<Role> { Role.Application("dispatcher") },
        5,
        Invitations: true);

    private static readonly string _longEmail = new string('a', 251) + "@b.c";

    public static TheoryData<string, string> Refused => new()
    {
        { """{}""", "/roles" },
        { """{"roles":"dispatcher"}""", "/roles" },
        { """{"roles":["technician","technician"]}""", "/roles/1" },
        { """{"roles":["admin"]}""", "/roles/0" },
        { """{"roles":["auditor"]}""", "/roles/0" },
        { """{"roles":[],"maxUses":0}""", "/maxUses" },
        { """{"roles":[],"maxUses":101}""", "/maxUses" },
        { """{"roles":[],"maxUses":1.5}""", "/maxUses" },
        { """{"roles":[],"maxUses":"2"}""", "/maxUses" },
        { """{"roles":[],"expiresAt":"2026-11-13T08:59:59Z"}""", "/expiresAt" },
        { """{"roles":[],"expiresAt":"2026-11-13T09:00:00Z"}""", "/expiresAt" },
        { """{"roles":[],"expiresAt":"2026-12-13T09:00:01Z"}""", "/expiresAt" },
        { """{"roles":[],"expiresAt":"2026-11-20T09:00:00"}""", "/expiresAt" },
        { """{"roles":[],"expiresAt":"next tuesday"}""", "/expiresAt" },
        { """{"roles":[],"expiresAt":1763024400}""", "/expiresAt" },
        { """{"roles":[],"email":"a"}""", "/email" },
        { """{"roles":[],"email":"a@b@c"}""", "/email" },
        { $$"""{"roles":[],"email":"{{_longEmail}}"}""", "/email" },
        { """{"roles":[],"email":"a b@c"}""", "/email" },
        { """{"roles":[],"email":"@b"}""", "/email" },
        { """{"roles":[],"email":"a@"}""", "/email" },
        { """{"roles":[],"email":42}""", "/email" },
        { """{"roles":[],"isOwner":"yes"}""", "/isOwner" },
        { """{"roles":[],"tenantId":"x"}""", "/tenantId" },
    };

    [Fact]
    public void Roles_alone_take_every_default()
    {
        var invitation = Valid("""{"roles":["technician"]}""");

        invitation.Roles.ShouldBe(["technician"]);
        invitation.IsOwner.ShouldBeFalse();
        invitation.Email.ShouldBeNull();
        invitation.MaxUses.ShouldBe(1);
        invitation.ExpiresAt.ShouldBe(_now.AddDays(7));
    }

    [Fact]
    public void Every_member_is_read_and_the_email_is_trimmed_and_lower_cased()
    {
        var invitation = Valid(
            """{"roles":["dispatcher","technician"],"email":"  Ada@Gmail.COM ","maxUses":100,"expiresAt":"2026-12-13T10:00:00+01:00"}""");

        invitation.Roles.ShouldBe(["dispatcher", "technician"]);
        invitation.Email.ShouldBe("ada@gmail.com");
        invitation.MaxUses.ShouldBe(100);
        invitation.ExpiresAt.ShouldBe(_now.AddDays(30), "30 days exactly is the latest");
        var nulls = Valid("""{"roles":[],"email":null,"maxUses":null,"expiresAt":null}""");
        (nulls.Email, nulls.MaxUses, nulls.ExpiresAt).ShouldBe((null, 1, _now.AddDays(7)), "null is the default");
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
    public void An_owner_granting_invitation_is_single_use_and_at_most_seven_days()
    {
        var sevenDays = _now.AddDays(7).ToString("O", CultureInfo.InvariantCulture);
        var later = _now.AddDays(7).AddSeconds(1).ToString("O", CultureInfo.InvariantCulture);

        Pointers("""{"roles":[],"isOwner":true,"maxUses":2}""").ShouldBe(["/maxUses"]);
        Pointers($$"""{"roles":[],"isOwner":true,"expiresAt":"{{later}}"}""").ShouldBe(["/expiresAt"]);
        var owner = Valid("""{"roles":["dispatcher"],"isOwner":true}""");
        owner.IsOwner.ShouldBeTrue();
        owner.MaxUses.ShouldBe(1);
        owner.ExpiresAt.ShouldBe(_now.AddDays(7));
        Valid($$"""{"roles":[],"isOwner":true,"maxUses":1,"expiresAt":"{{sevenDays}}"}""").ExpiresAt.ShouldBe(_now.AddDays(7));
    }

    [Fact]
    public void Every_violation_of_one_body_is_reported_together()
    {
        Pointers("""{"roles":["admin"],"maxUses":0,"expiresAt":"x","email":"y"}""")
            .ShouldBe(["/roles/0", "/email", "/maxUses", "/expiresAt"]);
    }

    [Fact]
    public void No_violation_echoes_what_was_sent()
    {
        const string Body = """{"roles":["secretrole1"],"email":"secret2@x@y","maxUses":7777,"expiresAt":"secret3","secret4":1}""";

        var (_, violations) = Validate(Body);

        violations.Count.ShouldBe(5);
        foreach (var text in violations.SelectMany(violation => new[] { violation.Message, violation.FixSuggestion ?? string.Empty }))
        {
            text.ShouldNotContain("secret");
            text.ShouldNotContain("7777");
        }
    }

    private static InvitationRequest Valid(string json)
    {
        var (value, violations) = Validate(json);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(violation => $"{violation.Pointer}: {violation.Message}")));
        return value.ShouldNotBeNull();
    }

    private static string[] Pointers(string json) => [.. Validate(json).Violations.Select(violation => violation.Pointer)];

    private static (InvitationRequest? Value, IReadOnlyList<AlvoViolation> Violations) Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return InvitationRequest.Validate(document.RootElement, _policy, _now);
    }
}
