namespace MMLib.Alvo.Identity;

/// <summary>
/// The identity subsystem's two wire-level names: its configuration section and its DI key.
/// </summary>
public static class AlvoIdentity
{
    /// <summary>The configuration section the bootstrap administrator is configured from.</summary>
    /// <remarks>
    /// The <c>Alvo:*</c> spelling the host already binds, not the <c>ALVO_ADMIN_*</c> names spec §X.1
    /// sketches: <c>docs/architecture/host.md</c> §Configuration deviated once deliberately, and #233
    /// owns the vocabulary question globally. A third spelling would be worse than either.
    /// </remarks>
    public const string ConfigurationSection = "Alvo:Admin";

    /// <summary>
    /// The DI key the cookie <see cref="MMLib.Alvo.Auth.IAlvoContextResolver"/> is registered under.
    /// </summary>
    /// <remarks>
    /// <b>Keyed, and that is a security decision rather than a composition style.</b> The unkeyed
    /// <see cref="MMLib.Alvo.Auth.IAlvoContextResolver"/> is what the Data API hands the raw <c>X-Alvo-Api-Key</c>
    /// header to. This resolver's credential is a subject identifier ASP.NET Core has
    /// <em>already</em> authenticated, so reaching it from that header would turn "knows an
    /// operator's uuid" into a working credential. Registering it under a key means the header path
    /// cannot resolve it at all.
    /// </remarks>
    public const string ResolverKey = "alvo-identity";
}
