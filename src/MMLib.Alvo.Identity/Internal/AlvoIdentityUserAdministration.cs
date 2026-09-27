using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// <see cref="IAlvoUserAdministration"/> over ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>It authorizes nothing, and that is the design rather than an omission.</b> Every guard —
/// whether the caller may administer at all, whether they are granting themselves a level, whether
/// the target is the bootstrap administrator — lives in the core's decorator. A rule enforced
/// inside a swappable adapter is optional by construction: the second implementation simply does
/// not have it.
/// </para>
/// <para>
/// It is registered under <see cref="AlvoUserAdministration.UnguardedKey"/> precisely so that
/// nothing but that decorator can resolve it.
/// </para>
/// <para>
/// <b>Every write is its own unit of work</b> through <see cref="AlvoIdentityUnitOfWork"/>, and a write
/// that lost a race to another administrator's is refused as
/// <see cref="MMLib.Alvo.Data.AlvoPreconditionFailedException"/> — never as a raw EF exception, and
/// never leaving rows behind for the next write to trip over.
/// </para>
/// </remarks>
/// <param name="users">Identity's user manager over the Alvo identity store.</param>
/// <param name="roles">Identity's role manager, for the rows a membership needs.</param>
/// <param name="store">The identity store, for the paged read.</param>
/// <param name="bootstrap">Who the bootstrap administrator is, for the refusals the core applies.</param>
internal sealed class AlvoIdentityUserAdministration(
    UserManager<AlvoIdentityUser> users,
    RoleManager<AlvoIdentityRole> roles,
    AlvoIdentityDbContext store,
    IAlvoBootstrapAdmin bootstrap) : IAlvoUserAdministration
{
    /// <inheritdoc/>
    public async Task<AlvoUserPage> ListAsync(
        AlvoUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = store.Users.AsNoTracking().OrderBy(row => row.Email).AsQueryable();

        if (query.Search is { Length: > 0 } search)
        {
            rows = rows.Where(row => row.Email != null && row.Email.Contains(search));
        }

        /* The cursor is the last address seen. Keyset over a unique ordered column, which is what
           the Data API's own paging does and for the same reason: an offset drifts under a
           concurrent insert, and a page that silently skips a row is worse than a slow one. */
        if (query.After is { Length: > 0 } after)
        {
            /* The two-argument Compare, which EF translates to SQL's own ">" — so the cursor is
               compared by the database's collation, the one that ordered the page. A comparison
               chosen here would be a different order from the one the page was built with, which is
               how a keyset cursor starts skipping rows; and the Ordinal overload this line once used
               has no translation at all, so every second page threw. */
            /* CA1309 is about a comparison .NET runs; this one is an expression tree SQL runs. */
#pragma warning disable CA1309
            rows = rows.Where(row => string.Compare(row.Email, after) > 0);
#pragma warning restore CA1309
        }

        var total = await rows.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var page = await rows.Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        var more = page.Count > limit;
        var taken = more ? page[..limit] : page;

        var projected = new List<AlvoUser>(taken.Count);
        foreach (var row in taken)
        {
            projected.Add(await ProjectAsync(row).ConfigureAwait(false));
        }

        return new AlvoUserPage(projected, more ? taken[^1].Email : null, total);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> CreateAsync(
        AlvoUserCreation creation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);
        return AlvoIdentityUnitOfWork.RunAsync(store, () => CreateRowAsync(creation));
    }

    /// <inheritdoc cref="CreateAsync"/>
    private async Task<AlvoUser> CreateRowAsync(AlvoUserCreation creation)
    {
        var row = new AlvoIdentityUser
        {
            Id = Guid.CreateVersion7(),
            UserName = creation.Email,
            Email = creation.Email,
            TenantId = creation.Tenant?.Value,
        };

        /* No password. The account exists and cannot sign in until somebody sets one through a
           credential token — which is the whole point: an administrator never types a colleague's
           password, because a credential that travels as a value is readable by whoever handles it. */
        Succeeded(await users.CreateAsync(row).ConfigureAwait(false), creation.Email);

        if (creation.RoleNames.Count > 0)
        {
            await EnsureRolesExistAsync(creation.RoleNames).ConfigureAwait(false);
            Succeeded(
                await users.AddToRolesAsync(row, creation.RoleNames).ConfigureAwait(false),
                creation.Email);
        }

        return await ProjectAsync(row).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetRolesAsync(
        UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        return AlvoIdentityUnitOfWork.RunAsync(store, () => ReplaceRolesAsync(user, roleNames));
    }

    /// <inheritdoc cref="SetRolesAsync"/>
    private async Task<AlvoUser> ReplaceRolesAsync(UserId user, IReadOnlyList<string> roleNames)
    {
        var row = await RequireAsync(user).ConfigureAwait(false);
        var existing = await users.GetRolesAsync(row).ConfigureAwait(false);

        Succeeded(await users.RemoveFromRolesAsync(row, existing).ConfigureAwait(false), user.ToString());
        if (roleNames.Count > 0)
        {
            await EnsureRolesExistAsync(roleNames).ConfigureAwait(false);
            Succeeded(await users.AddToRolesAsync(row, roleNames).ConfigureAwait(false), user.ToString());
        }

        return await ProjectAsync(row).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetTenantAsync(
        UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
        => AlvoIdentityUnitOfWork.RunAsync(store, async () =>
        {
            var row = await RequireAsync(user).ConfigureAwait(false);
            row.TenantId = tenant?.Value;
            Succeeded(await users.UpdateAsync(row).ConfigureAwait(false), user.ToString());
            return await ProjectAsync(row).ConfigureAwait(false);
        });

    /// <inheritdoc/>
    public Task<AlvoUser> SetDisabledAsync(
        UserId user, bool disabled, CancellationToken cancellationToken = default)
        => AlvoIdentityUnitOfWork.RunAsync(store, () => WriteDisabledAsync(user, disabled));

    /// <inheritdoc cref="SetDisabledAsync"/>
    private async Task<AlvoUser> WriteDisabledAsync(UserId user, bool disabled)
    {
        var row = await RequireAsync(user).ConfigureAwait(false);

        /* A lockout with no end is what "disabled" means here, and the resolver reads exactly that
           through AlvoIdentityLockout — which also says why a lockout *with* an end, the one failed
           sign-ins write, is deliberately not read as disabled. */
        Succeeded(
            await users.SetLockoutEnabledAsync(row, enabled: true).ConfigureAwait(false),
            user.ToString());
        Succeeded(
            await users.SetLockoutEndDateAsync(row, disabled ? AlvoIdentityLockout.Disabled : null)
                .ConfigureAwait(false),
            user.ToString());

        return await ProjectAsync(row).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<AlvoCredentialToken> IssueCredentialTokenAsync(
        UserId user, CancellationToken cancellationToken = default)
        => AlvoIdentityUnitOfWork.RunAsync(store, () => MintCredentialTokenAsync(user));

    /// <inheritdoc cref="IssueCredentialTokenAsync"/>
    private async Task<AlvoCredentialToken> MintCredentialTokenAsync(UserId user)
    {
        var row = await RequireAsync(user).ConfigureAwait(false);
        var token = await users.GeneratePasswordResetTokenAsync(row).ConfigureAwait(false);

        /* Identity's own lifetime for a data-protector token is one day, and it is not readable
           from here without reaching into the provider's options — so the expiry is stated as the
           default rather than computed, and a screen that renders it says "about". Overstating it
           would be worse than approximating it. */
        return new AlvoCredentialToken(user, token, DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Who the bootstrap administrator is, so the core's refusals can name them.</summary>
    /// <remarks>
    /// Exposed on the implementation rather than the port: the port is about administering people,
    /// and which of them is infrastructure is the host's fact, not the contract's.
    /// </remarks>
    public IAlvoBootstrapAdmin Bootstrap => bootstrap;

    /// <summary>
    /// Creates the identity role rows a membership needs, when they are absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalogue is the descriptor's; these rows are plumbing.</b> ASP.NET Core Identity
    /// refuses to assign a role whose row does not exist, so without this an administrator
    /// assigning a role the descriptor declares would be told <i>role does not exist</i> — which is
    /// true of the identity database and false of the project, and sends them looking for a screen
    /// that should not exist.
    /// </para>
    /// <para>
    /// <b>Creating a row grants nothing on its own.</b> A membership naming a role the descriptor
    /// does not declare is never minted into a caller's context — the resolver intersects with
    /// <c>IRoleCatalogProvider.DeclaredRoles</c> and fails closed. So this is safe by the same fact
    /// that makes <see cref="AlvoUser.RoleNames"/> a list of names rather than of roles.
    /// </para>
    /// </remarks>
    /// <param name="names">The roles a write is about to assign.</param>
    private async Task EnsureRolesExistAsync(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!await roles.RoleExistsAsync(name).ConfigureAwait(false))
            {
                Succeeded(
                    await roles.CreateAsync(new AlvoIdentityRole { Id = Guid.CreateVersion7(), Name = name })
                        .ConfigureAwait(false),
                    name);
            }
        }
    }

    private async Task<AlvoIdentityUser> RequireAsync(UserId user)
        => await users.FindByIdAsync(user.Value.ToString()).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"There is no user with id '{user}'.");

    private async Task<AlvoUser> ProjectAsync(AlvoIdentityUser row) => new()
    {
        Id = new UserId(row.Id),
        Email = row.Email ?? row.UserName ?? string.Empty,
        RoleNames = [.. await users.GetRolesAsync(row).ConfigureAwait(false)],
        IsDisabled = AlvoIdentityLockout.IsDisabled(row.LockoutEnd),
        Tenant = row.TenantId is { } tenant ? new TenantId(tenant) : null,
    };

    private static void Succeeded(IdentityResult result, string who)
    {
        if (result.Succeeded)
        {
            return;
        }

        AlvoIdentityUnitOfWork.ThrowIfRaced(result);
        var reasons = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException($"The account '{who}' could not be written: {reasons}");
    }
}
