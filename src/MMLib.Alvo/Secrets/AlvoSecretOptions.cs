namespace MMLib.Alvo.Secrets;

/// <summary>
/// How a deployment supplies its secrets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Infrastructure, never the descriptor.</b> The descriptor names a secret; where the value comes from is
/// a property of the deployment, and PLAN §4's invariant keeps the two apart.
/// </para>
/// <para>
/// <b>The key-encryption key is a file, and a key written into configuration is refused.</b> That is this
/// repository's own precedent rather than a new opinion — <c>Alvo__Admin__BootstrapPassword</c> is refused
/// by name for the reason that applies harder here: a value in configuration is a value in an environment
/// dump, a process listing and a crash report, and §7.1 asks for exactly the opposite ("secrets never into
/// logs, env dumps or git").
/// </para>
/// </remarks>
public sealed class AlvoSecretOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string ConfigurationSection = "Alvo:Secrets";

    /// <summary>
    /// Gets or sets the path to the file holding the key the database-backed store encrypts with — 32 bytes,
    /// base64.
    /// </summary>
    /// <remarks>
    /// A mounted Kubernetes secret, a workload identity's file, a Docker secret. §7.1's answer to the
    /// bootstrap paradox is that the credential for the secret store comes from the platform, never from
    /// another secret store — so this is a path, and with no file there is no writable store at all rather
    /// than a fallback key.
    /// </remarks>
    public string? EncryptionKeyFile { get; set; }

    /// <summary>
    /// Gets or sets the secrets this deployment supplies through configuration itself, by name.
    /// </summary>
    /// <remarks>
    /// The GitOps door, and the one every cloud vault already fits through: Key Vault, a K8s secret and a
    /// user-secrets file all reach <c>IConfiguration</c> through a provider the host adds, so Alvo needs no
    /// adapter of its own for any of them. A name set here <b>wins</b> over the writable store, and a write
    /// to a name it carries is refused rather than stored and never read.
    /// </remarks>
    public IDictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
