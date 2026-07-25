namespace MMLib.Alvo.Auth;

/// <summary>
/// Options for the framework's built-in dev API-key auth mechanism: a fixed list of keys
/// configured in-process rather than issued and persisted by a provider. Configuration-bindable
/// so a host can populate <see cref="DevKeys"/> from <c>appsettings.json</c> or environment
/// variables.
/// </summary>
public sealed class AlvoAuthOptions
{
    /// <summary>Gets the configured dev API keys.</summary>
    public IList<AlvoDevApiKey> DevKeys { get; } = new List<AlvoDevApiKey>();

    /// <summary>Gets the HTTP header a presented API key is read from, consumed by the HTTP Data API.</summary>
    public string HeaderName { get; init; } = "X-Alvo-Api-Key";
}

/// <summary>A single dev API key, as configured directly rather than issued by a provider.</summary>
public sealed class AlvoDevApiKey
{
    /// <summary>Gets or sets the key's public identifier.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plaintext secret as configured; retained on the options instance for the
    /// process lifetime — a dev mechanism only, not a production issuance path.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Gets or sets the user this key authenticates as.</summary>
    public Guid User { get; set; }

    /// <summary>Gets the names of the roles this key grants.</summary>
    public IList<string> Roles { get; } = new List<string>();

    /// <summary>Gets or sets the tenant this key is scoped to, if any.</summary>
    public Guid? Tenant { get; set; }

    /// <summary>Gets the entity/access scopes this key grants, in the descriptor form <c>"&lt;entity|*&gt;:&lt;read|write&gt;"</c>.</summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>Gets or sets when this key expires, if ever.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
