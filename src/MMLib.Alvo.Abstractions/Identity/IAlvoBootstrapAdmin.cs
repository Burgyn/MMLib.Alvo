namespace MMLib.Alvo;

/// <summary>
/// Answers whether a caller is the deployment's bootstrap administrator — the one identity the
/// descriptor's <c>access</c> block does not govern.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port rather than a role, because a role is descriptor-mintable.</b> The bootstrap admin is
/// infrastructure configuration (<c>Alvo:Admin:BootstrapEmail</c>), and
/// <em>Descriptor ≠ infra config</em> is invariant 4 of <c>docs/PLAN.md</c>. Reading it off
/// <see cref="Role.Admin"/> instead would make every credential that names the built-in
/// <c>admin</c> role a management bypass, which is the opposite of what the bootstrap exists for.
/// </para>
/// <para>
/// <b>The default implementation answers <see langword="false"/> for everyone</b>, so a host
/// without an identity subsystem has no bypass at all. An implementation must never answer
/// <see langword="true"/> for the reserved all-zero <see cref="UserId"/>, which means "no
/// identity".
/// </para>
/// </remarks>
public interface IAlvoBootstrapAdmin
{
    /// <summary>Answers whether <paramref name="user"/> is the configured bootstrap administrator.</summary>
    /// <param name="user">The caller's internal identifier.</param>
    /// <returns><see langword="true"/> when the caller is the bootstrap administrator.</returns>
    bool IsBootstrapAdmin(UserId user);
}
