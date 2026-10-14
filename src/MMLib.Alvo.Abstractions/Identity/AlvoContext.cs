namespace MMLib.Alvo;

/// <summary>
/// The identity every data operation is performed as: who the caller is, which roles they
/// hold, and which tenant they act in. Passed explicitly to <c>IAlvoData</c> rather than read
/// from ambient state, because the post-commit paths (outbox dispatcher, after-hooks,
/// automation actions) run with no request scope and are exactly where a wrong or missing
/// tenant is catastrophic.
/// </summary>
public sealed record AlvoContext
{
    private static readonly UserId _systemUser = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));

    private readonly IReadOnlySet<Role> _roles = new HashSet<Role> { Role.Anon };

    private readonly AlvoActorKind? _actorKind;

    private readonly AlvoProvenance _provenance = AlvoProvenance.None;

    /// <summary>Gets the caller's internal identifier.</summary>
    public required UserId User { get; init; }

    /// <summary>
    /// Gets the roles the caller holds; never empty, since anonymous is the role
    /// <see cref="Role.Anon"/> rather than the absence of one.
    /// </summary>
    public required IReadOnlySet<Role> Roles
    {
        get => _roles;
        init => _roles = value is { Count: > 0 }
            ? value
            : throw new ArgumentException(
                "A caller always holds at least one role; use { Role.Anon } for an anonymous caller.",
                nameof(Roles));
    }

    /// <summary>Gets the tenant the caller acts in; <see langword="null"/> denies on a tenant-scoped entity.</summary>
    public TenantId? Tenant { get; init; }

    /// <summary>
    /// Gets how the caller authenticated. Unless set, it is derived from <see cref="User"/>: the anonymous caller is
    /// <see cref="AlvoActorKind.Anonymous"/>, the system caller <see cref="AlvoActorKind.System"/>, and any other
    /// <see cref="AlvoActorKind.ApiKey"/> — so a context a host builds by hand keeps the meaning it always had.
    /// </summary>
    public AlvoActorKind ActorKind
    {
        get => _actorKind ?? DerivedActorKind(User);
        init => _actorKind = value;
    }

    /// <summary>Gets the flow this caller acts in; <see cref="AlvoProvenance.None"/> unless one is given.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public AlvoProvenance Provenance
    {
        get => _provenance;
        init => _provenance = value ?? throw new ArgumentNullException(nameof(Provenance));
    }

    /// <summary>
    /// The anonymous caller: the reserved all-zero <see cref="UserId"/> — which means "no identity",
    /// never a caller who owns the all-zero rows (see <see cref="UserId"/>'s own remarks) — holding
    /// only <see cref="Role.Anon"/>. An operation whose policy reads <c>@user.id</c> is therefore
    /// denied for this caller rather than evaluated against the all-zero uuid.
    /// </summary>
    public static AlvoContext Anonymous { get; } = new()
    {
        User = default,
        Roles = new HashSet<Role> { Role.Anon },
    };

    /// <summary>
    /// The framework's own identity, for actions that run as the system rather than as the
    /// originator of a change (spec §3.3).
    /// </summary>
    /// <param name="tenant">The tenant the action operates in.</param>
    public static AlvoContext System(TenantId? tenant) => new()
    {
        User = _systemUser,
        Roles = new HashSet<Role> { Role.Admin },
        Tenant = tenant,
    };

    private static AlvoActorKind DerivedActorKind(UserId user) =>
        user == default ? AlvoActorKind.Anonymous
        : user == _systemUser ? AlvoActorKind.System
        : AlvoActorKind.ApiKey;

    /// <summary>Answers whether the caller holds a role.</summary>
    /// <param name="role">The role to test.</param>
    public bool HasRole(Role role) => Roles.Contains(role);
}
