namespace MMLib.Alvo;

/// <summary>
/// A person who can sign in and administer a project: an internal identifier, the address they
/// sign in with, and the role <em>names</em> they have been assigned.
/// </summary>
/// <remarks>
/// <b>Names, not <see cref="Role"/> values, and that is the port's load-bearing choice.</b> A
/// <see cref="Role"/> can only be minted through a <see cref="RoleCatalog"/>, so storing one would
/// force this port to hold the descriptor's catalog and to reject — at read time — a membership row
/// that a later descriptor revision removed the role for. Membership outlives a descriptor edit;
/// the intersection with <see cref="IRoleCatalogProvider.DeclaredRoles"/> happens where the
/// <see cref="AlvoContext"/> is minted, and a name nothing declares is simply not granted.
/// </remarks>
public sealed record AlvoUser
{
    /// <summary>Gets the user's internal identifier — never the reserved all-zero value.</summary>
    public required UserId Id { get; init; }

    /// <summary>Gets the address this user signs in with.</summary>
    public required string Email { get; init; }

    /// <summary>Gets the names of the roles this user has been assigned, declared or not.</summary>
    public required IReadOnlyList<string> RoleNames { get; init; }

    /// <summary>Gets a value indicating whether this user is barred from signing in.</summary>
    public bool IsDisabled { get; init; }
}
