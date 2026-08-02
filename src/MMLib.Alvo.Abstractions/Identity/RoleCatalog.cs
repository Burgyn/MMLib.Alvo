using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo;

/// <summary>
/// The closed set of roles this project recognises: the three built-ins plus the
/// descriptor's <c>auth.roles</c>. The only place an application <see cref="Role"/> can be
/// minted, so an undeclared name is rejected at the boundary where it arrives.
/// </summary>
/// <remarks>
/// Names are looked up with <see cref="StringComparer.Ordinal"/> — byte-for-byte, case-sensitive,
/// culture-invariant, the same rule <c>'editor' in @user.roles</c> is evaluated under in both Rule
/// backends. <c>Editor</c> is therefore not <c>editor</c>: it is a different name, and an undeclared
/// one. A case-insensitive catalog would make a role literal in a rule and the role on a credential
/// match under one comparer and not the other, which is how a caller silently gains or loses access.
/// </remarks>
public sealed class RoleCatalog
{
    private readonly Dictionary<string, Role> _byName;

    private RoleCatalog(IEnumerable<string> applicationRoles)
    {
        _byName = new Dictionary<string, Role>(StringComparer.Ordinal)
        {
            [Role.Anon.Name] = Role.Anon,
            [Role.Authenticated.Name] = Role.Authenticated,
            [Role.Admin.Name] = Role.Admin,
        };

        foreach (var name in applicationRoles)
        {
            _byName.TryAdd(name, Role.Application(name));
        }

        All = _byName.Values.ToHashSet();
    }

    /// <summary>A catalog holding only the three built-in roles.</summary>
    public static RoleCatalog BuiltInOnly { get; } = new([]);

    /// <summary>Gets every role this project recognises.</summary>
    public IReadOnlySet<Role> All { get; }

    /// <summary>Builds a catalog from a descriptor's <c>auth.roles</c>.</summary>
    /// <param name="descriptor">The descriptor to read roles from.</param>
    public static RoleCatalog FromDescriptor(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Create(descriptor.Auth?.Roles ?? []);
    }

    /// <summary>Builds a catalog from an explicit list of application role names.</summary>
    /// <param name="applicationRoles">Role names beyond the built-ins.</param>
    public static RoleCatalog Create(IEnumerable<string> applicationRoles) => new(applicationRoles);

    /// <summary>Looks a role up by name.</summary>
    /// <param name="name">The role name.</param>
    /// <param name="role">The resolved role when known.</param>
    /// <returns><see langword="true"/> when the name is a declared role.</returns>
    public bool TryGet(string name, out Role role) => _byName.TryGetValue(name, out role);

    /// <summary>Resolves a role by name.</summary>
    /// <param name="name">The role name.</param>
    /// <exception cref="UnknownRoleException">The name is not a declared role.</exception>
    public Role Get(string name) =>
        TryGet(name, out var role) ? role : throw new UnknownRoleException(name, KnownNames());

    /// <summary>Resolves a set of role names.</summary>
    /// <param name="names">The role names.</param>
    /// <exception cref="UnknownRoleException">Any name is not a declared role.</exception>
    public IReadOnlySet<Role> Resolve(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Select(Get).ToHashSet();
    }

    private string[] KnownNames() =>
        _byName.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// <summary>Thrown when a role name is not declared in the active <see cref="RoleCatalog"/>.</summary>
public sealed class UnknownRoleException : Exception
{
    /// <summary>Initializes a new instance of <see cref="UnknownRoleException"/>.</summary>
    public UnknownRoleException()
    {
        RoleName = string.Empty;
    }

    /// <summary>Initializes a new instance of <see cref="UnknownRoleException"/> with a message.</summary>
    /// <param name="message">The exception message.</param>
    public UnknownRoleException(string message)
        : base(message)
    {
        RoleName = string.Empty;
    }

    /// <summary>Initializes a new instance of <see cref="UnknownRoleException"/> with a message and inner exception.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public UnknownRoleException(string message, Exception innerException)
        : base(message, innerException)
    {
        RoleName = string.Empty;
    }

    internal UnknownRoleException(string roleName, IReadOnlyList<string> knownRoles)
        : base($"Role '{roleName}' is not declared. Declared roles: {string.Join(", ", knownRoles)}. Add it to auth.roles in the descriptor.")
    {
        RoleName = roleName;
    }

    /// <summary>Gets the role name that was not declared.</summary>
    public string RoleName { get; }
}
