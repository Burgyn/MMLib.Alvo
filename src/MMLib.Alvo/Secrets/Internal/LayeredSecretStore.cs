namespace MMLib.Alvo.Secrets.Internal;

/// <summary>
/// The store every caller resolves: configuration first, then the writable store a driver registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration wins, and that is the product's own precedence.</b> A file or an environment a
/// deployment controls beats a record a screen wrote — the same order the AI connection resolves under, so
/// there is one rule to learn rather than two.
/// </para>
/// <para>
/// <b>A write to a name configuration shadows is refused by name.</b> It is the single failure mode this
/// layering can produce, and the silent version is the worst kind: the operator saves a key, the screen says
/// saved, and every request keeps using the old one. It fails closed and it says which name.
/// </para>
/// </remarks>
/// <param name="configuration">The read-only layer, consulted first.</param>
/// <param name="writable">The writable layer, or <see langword="null"/> when the deployment has none.</param>
internal sealed class LayeredSecretStore(ISecretStore configuration, IWritableSecretStore? writable) : ISecretStore
{
    /// <inheritdoc/>
    public bool CanWrite => writable is { CanWrite: true };

    /// <inheritdoc/>
    public async ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        return await configuration.GetAsync(name, ct).ConfigureAwait(false)
            ?? (writable is null ? null : await writable.GetAsync(name, ct).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await RefuseIfShadowedAsync(name, ct).ConfigureAwait(false);
        await Writable().SetAsync(name, value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await RefuseIfShadowedAsync(name, ct).ConfigureAwait(false);

        return await Writable().DeleteAsync(name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>The union of both layers, each name once — a name in both is one secret, not two.</remarks>
    public async ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default)
    {
        var names = new Dictionary<string, SecretName>(StringComparer.Ordinal);
        foreach (var name in await configuration.ListNamesAsync(ct).ConfigureAwait(false))
        {
            names[name.Value] = name;
        }

        if (writable is not null)
        {
            foreach (var name in await writable.ListNamesAsync(ct).ConfigureAwait(false))
            {
                names[name.Value] = name;
            }
        }

        return [.. names.Values];
    }

    /// <summary>Refuses a write the configuration layer would make invisible.</summary>
    private async ValueTask RefuseIfShadowedAsync(SecretName name, CancellationToken ct)
    {
        if (await configuration.GetAsync(name, ct).ConfigureAwait(false) is not null)
        {
            throw new SecretShadowedException(name, $"{AlvoSecretOptions.ConfigurationSection}:Values");
        }
    }

    /// <summary>The writable layer, or the refusal naming what this deployment would have to mount.</summary>
    private IWritableSecretStore Writable() => writable ?? throw new InvalidOperationException(
        "This deployment has no writable secret store, so a secret cannot be set from here. Mount a key file "
        + $"and point {AlvoSecretOptions.ConfigurationSection}:EncryptionKeyFile at it, or supply the value "
        + $"through {AlvoSecretOptions.ConfigurationSection}:Values.");
}
