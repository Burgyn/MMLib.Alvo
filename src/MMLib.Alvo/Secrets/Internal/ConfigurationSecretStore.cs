using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Secrets.Internal;

/// <summary>
/// The read-only half of the secret store: whatever the deployment's own configuration carries under
/// <c>Alvo:Secrets:Values</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One adapter for four stores.</b> Azure Key Vault, a mounted Kubernetes secret, a user-secrets file and
/// an environment variable all reach <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> through
/// a provider the host adds — so the swap point §7.1 asks for already exists above Alvo, and writing four
/// adapters here would be writing what the platform wrote.
/// </para>
/// <para>
/// It cannot write, and says so rather than throwing when asked: a deployment whose secrets are deployed
/// rather than clicked has no writable store, and the screen has to know before it offers a control.
/// </para>
/// </remarks>
internal sealed class ConfigurationSecretStore : ISecretStore
{
    private readonly IOptionsMonitor<AlvoSecretOptions> _options;

    /// <summary>Initializes the store over the bound options.</summary>
    /// <param name="options">The options, watched so a reloaded configuration is seen without a restart.</param>
    public ConfigurationSecretStore(IOptionsMonitor<AlvoSecretOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public bool CanWrite => false;

    /// <inheritdoc/>
    public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ValueTask.FromResult(
            _options.CurrentValue.Values.TryGetValue(name.Value, out var value) ? value : null);
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"This deployment's secrets come from configuration ({AlvoSecretOptions.ConfigurationSection}:Values), "
            + "which Alvo reads and never writes. Set the value where the configuration comes from, or give the "
            + $"deployment a writable store by mounting a key file at {AlvoSecretOptions.ConfigurationSection}"
            + ":EncryptionKeyFile.");

    /// <inheritdoc/>
    public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"This deployment's secrets come from configuration ({AlvoSecretOptions.ConfigurationSection}:Values), "
            + "which Alvo reads and never writes. Remove the value where the configuration comes from.");

    /// <inheritdoc/>
    /// <remarks>
    /// A key that is not a valid <see cref="SecretName"/> is skipped rather than throwing: configuration is
    /// a place people mistype, and one bad key must not make the whole listing — and therefore the settings
    /// screen — fail.
    /// </remarks>
    public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default)
    {
        var names = new List<SecretName>();
        foreach (var key in _options.CurrentValue.Values.Keys)
        {
            if (SecretName.TryParse(key, out var name))
            {
                names.Add(name!);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SecretName>>(names);
    }
}
