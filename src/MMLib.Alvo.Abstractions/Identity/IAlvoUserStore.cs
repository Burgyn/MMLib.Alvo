namespace MMLib.Alvo;

/// <summary>
/// The provider port over the people who may administer a project: who exists, and which roles
/// each of them is a member of.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns membership; it does not own the role catalogue.</b> That belongs to the descriptor,
/// through <see cref="IRoleCatalogProvider"/>, whose own remarks protect the boundary. A store may
/// hold a membership row for a role no descriptor declares; nothing here rejects it, and nothing
/// here grants it either.
/// </para>
/// <para>
/// <b>No credential appears on this port.</b> Verifying a password, rotating it, or federating to
/// an external provider is the implementation's business — an OIDC-backed implementation has no
/// password to verify at all, and a port that demanded one would foreclose it.
/// </para>
/// <para>
/// The <c>Alvo</c> prefix follows <c>IAlvoData</c>'s precedent, and here it also avoids a
/// real collision with ASP.NET Core Identity's own <c>IUserStore&lt;T&gt;</c>, which the default
/// implementation is built on.
/// </para>
/// </remarks>
public interface IAlvoUserStore
{
    /// <summary>Finds a user by internal identifier.</summary>
    /// <param name="user">The user's internal identifier.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The user, or <see langword="null"/> when no such user is stored.</returns>
    ValueTask<AlvoUser?> FindAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Finds a user by the address they sign in with.</summary>
    /// <param name="email">The sign-in address.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The user, or <see langword="null"/> when no such user is stored.</returns>
    ValueTask<AlvoUser?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Lists every stored user.</summary>
    /// <param name="cancellationToken">A token to cancel the listing.</param>
    /// <returns>Every user, in no significant order.</returns>
    ValueTask<IReadOnlyList<AlvoUser>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Replaces a user's role memberships with <paramref name="roleNames"/>.</summary>
    /// <param name="user">The user whose memberships to replace.</param>
    /// <param name="roleNames">The role names the user is a member of after this call.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>A task that completes when the memberships have been replaced.</returns>
    ValueTask SetRolesAsync(UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken);
}
