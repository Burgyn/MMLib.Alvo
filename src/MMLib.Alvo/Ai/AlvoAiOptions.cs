namespace MMLib.Alvo.Ai;

/// <summary>
/// The AI connection a deployment pins in its own configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Infrastructure, never the descriptor</b> (PLAN §4). A model name and an endpoint belong to the
/// deployment; a descriptor that carried them would stop applying the day the operator changed providers.
/// </para>
/// <para>
/// <b>The key is a reference, not a value.</b> <see cref="ApiKeySecretRef"/> names a secret the store
/// resolves, so the GitOps path still ends at a mounted file or a vault — writing the key here would be
/// writing it into an environment dump, which §7.1 asks to be impossible.
/// </para>
/// </remarks>
public sealed class AlvoAiOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string ConfigurationSection = "Alvo:Ai";

    /// <summary>
    /// Gets or sets which protocol the endpoint speaks — <c>openai-compatible</c> or <c>azure-openai</c>.
    /// </summary>
    /// <remarks>
    /// The wire spelling rather than the enum's, because this is what an operator types into a YAML file and
    /// <c>OpenAiCompatible</c> is not.
    /// </remarks>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the base address to dial, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Gets or sets the model or deployment name to ask for.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the name of the secret holding the API key, resolved through the secret store.
    /// </summary>
    /// <remarks>
    /// A name rather than a value, so a deployment whose secrets come from Key Vault, a mounted file or the
    /// encrypted table all configure the connection the same way — and none of them puts the credential in
    /// configuration.
    /// </remarks>
    public string? ApiKeySecretRef { get; set; }
}
