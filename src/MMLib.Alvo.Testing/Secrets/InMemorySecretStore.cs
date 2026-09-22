using MMLib.Alvo.Secrets;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Testing.Secrets;

/// <summary>
/// The reference <see cref="ISecretStore"/>: a dictionary, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It exists so <see cref="SecretStoreContractTests"/> has something to run against before either shipped
/// implementation does, and so a host under test can hold a secret without a database — the same role
/// <c>InMemoryDescriptorVersionStore</c> plays for its own port.
/// </para>
/// <para>
/// <b>Not for production, and it does not pretend otherwise:</b> nothing here is encrypted, nothing survives
/// the process, and every value sits in managed memory where a heap dump finds it.
/// </para>
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool CanWrite => true;

    /// <inheritdoc/>
    public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ValueTask.FromResult(_values.TryGetValue(name.Value, out var value) ? value : null);
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        _values[name.Value] = value;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ValueTask.FromResult(_values.TryRemove(name.Value, out _));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<SecretName>>(
            [.. _values.Keys.Select(SecretName.Parse)]);
}
