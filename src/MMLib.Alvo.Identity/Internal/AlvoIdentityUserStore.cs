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
        await users.RemoveFromRolesAsync(stored, current.Except(roleNames, StringComparer.Ordinal))
            .ConfigureAwait(false);
        await users.AddToRolesAsync(stored, roleNames.Except(current, StringComparer.Ordinal))
            .ConfigureAwait(false);
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
        };
}
