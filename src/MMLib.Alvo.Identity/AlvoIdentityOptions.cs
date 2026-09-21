namespace MMLib.Alvo.Identity;

/// <summary>
/// The identity subsystem's own configuration, bound from <see cref="AlvoIdentity.ConfigurationSection"/>.
/// </summary>
/// <remarks>
/// <b>Infrastructure, never the descriptor.</b> <c>docs/PLAN.md</c> invariant 4 is
/// <em>Descriptor ≠ infra config</em>, and the design brief lists "bootstrap admin credentials"
/// explicitly among infra config. A credential must never enter a descriptor, which is versioned and
/// exported.
/// </remarks>
public sealed class AlvoIdentityOptions
{
    /// <summary>Gets or sets the address of the bootstrap administrator, or <see langword="null"/> for none.</summary>
    public string? BootstrapEmail { get; set; }

    /// <summary>Gets or sets the path of the file holding the bootstrap administrator's password.</summary>
    /// <remarks>
    /// A file rather than a value, so the secret arrives as a mounted Docker/Kubernetes secret and never
    /// as an environment variable a process listing, a crash dump or a <c>docker inspect</c> can read.
    /// </remarks>
    public string? BootstrapPasswordFile { get; set; }
}
