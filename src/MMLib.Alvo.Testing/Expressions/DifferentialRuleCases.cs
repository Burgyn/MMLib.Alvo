using MMLib.Alvo.Data;

namespace MMLib.Alvo.Testing;

/// <summary>
/// One rule/row/caller combination the differential test replays against both backends: the
/// in-memory <c>WITH CHECK</c> interpreter and a rendered SQL <c>USING</c> predicate.
/// </summary>
/// <param name="Rule">The CEL source, compiled for the <c>Rule</c> profile against the differential fixture's entity.</param>
/// <param name="Row">The candidate row.</param>
/// <param name="ContextName">The caller, resolved via <see cref="DifferentialRuleCases.ContextFor"/>.</param>
public sealed record DifferentialRuleCase(string Rule, AlvoRecord Row, string ContextName);

/// <summary>
/// The shared rule/row/caller matrix that proves Alvo's two rule backends can never disagree —
/// PR1 replays it against an in-process SQL evaluator, PR2 replays the identical matrix against
/// real SQLite and PostgreSQL. Lives here, not in the test project, because a shared library
/// cannot reference a test project: the named callers (<see cref="ContextFor"/>) are defined once,
/// here, and <c>CelFixtures</c> in the test project delegates to them rather than redeclaring its
/// own copies.
/// </summary>
/// <remarks>
/// Every case is built against an entity shaped like the test project's <c>CelFixtures.Orders</c>
/// (<c>owner_id</c>, <c>status</c>, <c>total</c>, <c>title</c>, <c>tenant_id</c>, <c>created_at</c>,
/// <c>approved_at</c>, <c>is_public</c>) — this class does not carry the <c>EntitySchema</c> itself
/// (only <see cref="AlvoRecord"/> field/value pairs), so the caller compiles <see cref="DifferentialRuleCase.Rule"/>
/// against whatever schema its own fixture declares, as long as it declares the same field names.
/// </remarks>
public static class DifferentialRuleCases
{
    private static readonly RoleCatalog _roleCatalog = RoleCatalog.Create(["editor"]);

    private static readonly TenantId _acmeTenant = TenantId.New();

    private static readonly TenantId _otherTenant = TenantId.New();

    private static readonly AlvoContext _alice = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _acmeTenant,
    };

    private static readonly AlvoContext _bob = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _acmeTenant,
    };

    private static readonly AlvoContext _editor = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated, _roleCatalog.Get("editor") },
        Tenant = _acmeTenant,
    };

    private static readonly AlvoContext _admin = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        Tenant = _acmeTenant,
    };

    private static readonly AlvoContext _acmeUser = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _acmeTenant,
    };

    private static readonly AlvoContext _otherTenantUser = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _otherTenant,
    };

    private static readonly AlvoContext _tenantlessAlice = _alice with { Tenant = null };

    private static readonly Dictionary<string, AlvoContext> _contextsByName = new(StringComparer.Ordinal)
    {
        [nameof(Alice)] = _alice,
        [nameof(Bob)] = _bob,
        [nameof(Editor)] = _editor,
        [nameof(Admin)] = _admin,
        [nameof(AcmeUser)] = _acmeUser,
        [nameof(OtherTenantUser)] = _otherTenantUser,
        [nameof(TenantlessAlice)] = _tenantlessAlice,
    };

    /// <summary>An authenticated caller in the Acme tenant.</summary>
    public static AlvoContext Alice => _alice;

    /// <summary>A second, distinct authenticated caller in the Acme tenant — never <see cref="Alice"/>'s own row.</summary>
    public static AlvoContext Bob => _bob;

    /// <summary>An Acme-tenant caller holding the declared application role <c>editor</c>.</summary>
    public static AlvoContext Editor => _editor;

    /// <summary>An Acme-tenant caller holding <see cref="Role.Admin"/> plus <see cref="Role.Authenticated"/>.</summary>
    public static AlvoContext Admin => _admin;

    /// <summary>A plain authenticated caller in the Acme tenant, for tenant-isolation cases.</summary>
    public static AlvoContext AcmeUser => _acmeUser;

    /// <summary>An authenticated caller in a different tenant than <see cref="AcmeUser"/>, for tenant-isolation cases.</summary>
    public static AlvoContext OtherTenantUser => _otherTenantUser;

    /// <summary><see cref="Alice"/> with no tenant — denied on a tenant-scoped entity, never widened to "all tenants".</summary>
    public static AlvoContext TenantlessAlice => _tenantlessAlice;

    /// <summary>
    /// Resolves a caller by the name a <see cref="DifferentialRuleCase.ContextName"/> carries —
    /// <c>Alice</c>, <c>Bob</c>, <c>Editor</c>, <c>Admin</c>, <c>AcmeUser</c>, <c>OtherTenantUser</c>,
    /// or <c>TenantlessAlice</c>.
    /// </summary>
    /// <param name="name">The caller's name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of the named callers.</exception>
    public static AlvoContext ContextFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _contextsByName.TryGetValue(name, out var context)
            ? context
            : throw new ArgumentException($"'{name}' is not a differential-fixture caller.", nameof(name));
    }

    /// <summary>
    /// The shared rule/row/caller matrix: every shape the two backends could plausibly disagree on —
    /// a nullable field under <c>==</c>/<c>!=</c>/negation, a null operand on both sides of
    /// <c>&amp;&amp;</c>/<c>||</c>, <c>has()</c> on an absent/present-null/present-value field, role
    /// membership present/absent, tenant match/mismatch/absent, a nullable boolean field bare/negated/
    /// in a conjunction, a cross-type numeric comparison, a field-to-field timestamp comparison, a
    /// field-backed role-membership match, and a nested <c>(a || b) &amp;&amp; !c</c> tree.
    /// </summary>
    public static IReadOnlyList<DifferentialRuleCase> All { get; } = BuildCases();

    private static AlvoRecord Row(params (string Field, object? Value)[] fields) =>
        new(fields.ToDictionary(pair => pair.Field, pair => pair.Value));

    private static IReadOnlyList<DifferentialRuleCase> BuildCases()
    {
        var aliceId = _alice.User.Value;
        var bobId = _bob.User.Value;
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        return
        [
            new("owner_id == @user.id", Row(("owner_id", aliceId)), nameof(Alice)),
            new("owner_id == @user.id", Row(("owner_id", bobId)), nameof(Alice)),
            new("owner_id == @user.id", Row(("owner_id", null)), nameof(Alice)),
            new("owner_id != @user.id", Row(("owner_id", null)), nameof(Alice)),
            new("!(owner_id == @user.id)", Row(("owner_id", null)), nameof(Alice)),
            new("owner_id == @user.id && status == 'approved'", Row(("owner_id", null), ("status", "approved")), nameof(Alice)),
            new("status == 'approved' && owner_id == @user.id", Row(("owner_id", null), ("status", "approved")), nameof(Alice)),
            new("owner_id == @user.id || status == 'approved'", Row(("owner_id", null), ("status", "approved")), nameof(Alice)),
            new("status == 'approved' || owner_id == @user.id", Row(("owner_id", null), ("status", "approved")), nameof(Alice)),
            new("has(owner_id)", Row(), nameof(Alice)),
            new("has(owner_id)", Row(("owner_id", null)), nameof(Alice)),
            new("has(owner_id)", Row(("owner_id", aliceId)), nameof(Alice)),
            new("'editor' in @user.roles", Row(), nameof(Editor)),
            new("'editor' in @user.roles", Row(), nameof(Alice)),
            new("tenant_id == @tenant.id", Row(("tenant_id", _acmeTenant.Value)), nameof(AcmeUser)),
            new("tenant_id == @tenant.id", Row(("tenant_id", _otherTenant.Value)), nameof(AcmeUser)),
            new("tenant_id == @tenant.id", Row(("tenant_id", _acmeTenant.Value)), nameof(TenantlessAlice)),
            new("tenant_id == @tenant.id", Row(("tenant_id", null)), nameof(TenantlessAlice)),
            new("is_public", Row(("is_public", null)), nameof(Alice)),
            new("is_public", Row(("is_public", true)), nameof(Alice)),
            new("!is_public", Row(("is_public", null)), nameof(Alice)),
            new("is_public && owner_id == @user.id", Row(("is_public", null), ("owner_id", aliceId)), nameof(Alice)),
            new("total > 5", Row(("total", 10m)), nameof(Alice)),
            new("total > 5", Row(("total", null)), nameof(Alice)),
            new("created_at == approved_at", Row(("created_at", earlier), ("approved_at", earlier)), nameof(Alice)),
            new("created_at < approved_at", Row(("created_at", earlier), ("approved_at", later)), nameof(Alice)),
            new("created_at == approved_at", Row(("created_at", earlier), ("approved_at", null)), nameof(Alice)),
            new("status in @user.roles", Row(("status", "editor")), nameof(Editor)),
            new("status in @user.roles", Row(("status", "draft")), nameof(Editor)),
            new(
                "(owner_id == @user.id || status == 'approved') && !has(owner_id)",
                Row(("owner_id", null), ("status", "approved")),
                nameof(Alice)),
            new(
                "(owner_id == @user.id || status == 'approved') && !has(owner_id)",
                Row(("owner_id", aliceId), ("status", "draft")),
                nameof(Alice)),
        ];
    }
}
