using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The default <see cref="IAlvoUserStore"/>: membership read off ASP.NET Core Identity's own stores.
/// </summary>
/// <remarks>
/// Role names are returned exactly as stored, declared or not — the intersection with the descriptor's
/// catalogue happens in <see cref="AlvoIdentityContextResolver"/>, and filtering here would hide an
/// assignment the administration screen has to be able to show.
/// </remarks>
/// <param name="users">ASP.NET Core Identity's user manager.</param>
/// <param name="store">The identity store, for the listing query.</param>
internal sealed class AlvoIdentityUserStore(
    UserManager<AlvoIdentityUser> users,
    AlvoIdentityDbContext store) : IAlvoUserStore
{
    /// <inheritdoc/>
    public async ValueTask<AlvoUser?> FindAsync(UserId user, CancellationToken cancellationToken)
        => await ProjectAsync(await users.FindByIdAsync(user.Value.ToString()).ConfigureAwait(false))
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<AlvoUser?> FindByEmailAsync(string email, CancellationToken cancellationToken)
        => await ProjectAsync(await users.FindByEmailAsync(email).ConfigureAwait(false)).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AlvoUser>> ListAsync(CancellationToken cancellationToken)
    {
        var stored = await store.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var projected = new List<AlvoUser>(stored.Count);

        foreach (var user in stored)
        {
            projected.Add((await ProjectAsync(user).ConfigureAwait(false))!);
        }

        return projected;
    }

    /// <inheritdoc/>
    public async ValueTask SetRolesAsync(
        UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        var stored = await users.FindByIdAsync(user.Value.ToString()).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No user with id '{user}' is stored.");

        var current = await users.GetRolesAsync(stored).ConfigureAwait(false);

        Succeeded(
            await users.RemoveFromRolesAsync(stored, current.Except(roleNames, StringComparer.Ordinal))
                .ConfigureAwait(false),
            user);
        Succeeded(
            await users.AddToRolesAsync(stored, roleNames.Except(current, StringComparer.Ordinal))
                .ConfigureAwait(false),
            user);
    }

    /// <summary>
    /// Turns a refused membership change into a throw, because the port has nowhere to report one.
    /// </summary>
    /// <remarks>
    /// <b>ASP.NET Core Identity refuses without throwing.</b> <c>AddToRolesAsync</c> returns a failed
    /// <see cref="IdentityResult"/> for a role the user already holds under a different casing, and
    /// <c>RemoveFromRolesAsync</c> does the same for one they do not hold; the concurrency stamp fails
    /// the same way. <see cref="IAlvoUserStore.SetRolesAsync"/> returns a bare <see cref="ValueTask"/>,
    /// so a discarded result leaves the administration screen showing a grant that never happened —
    /// which is the direction that matters, since the caller is an authorization decision's input.
    /// </remarks>
    /// <param name="result">What Identity said.</param>
    /// <param name="user">The user whose memberships were being replaced.</param>
    /// <exception cref="InvalidOperationException">The change was refused.</exception>
    private static void Succeeded(IdentityResult result, UserId user)
    {
        if (result.Succeeded)
        {
            return;
        }

        var reasons = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException(
            $"The role memberships of user '{user}' could not be replaced: {reasons}");
    }

    /// <summary>Projects a stored row onto the port's <see cref="AlvoUser"/>.</summary>
    /// <param name="stored">The stored row, or <see langword="null"/> for a miss.</param>
    /// <returns>The projected user, or <see langword="null"/> when there was no row.</returns>
    private async ValueTask<AlvoUser?> ProjectAsync(AlvoIdentityUser? stored) =>
        stored is null ? null : new AlvoUser
        {
            Id = new UserId(stored.Id),
            Email = stored.Email ?? stored.UserName ?? string.Empty,
            RoleNames = [.. await users.GetRolesAsync(stored).ConfigureAwait(false)],
            IsDisabled = stored.LockoutEnd is { } until && until > DateTimeOffset.UtcNow,
            Tenant = stored.TenantId is { } tenant ? new TenantId(tenant) : null,
        };
}
