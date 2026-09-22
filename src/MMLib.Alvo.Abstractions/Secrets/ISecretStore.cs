namespace MMLib.Alvo.Secrets;

/// <summary>
/// Where Alvo keeps the values a descriptor may only name — a model provider's key, a webhook's signing
/// secret, whatever a deployment has to hold and must not commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port because a secret store is the example the boundary rule gives</b>
/// (<c>docs/architecture/package-boundary.md</c>, rule (b)): a deployment on Azure reaches Key Vault, one on
/// Kubernetes a mounted secret, one on a laptop a file — and the core must not know which.
/// </para>
/// <para>
/// <b>Names are listed; values are not.</b> There is no bulk read, and <see cref="ListNamesAsync"/> answers
/// with names alone. A settings screen needs the names; nothing in this build needs every value at once, and
/// the one thing that would is an exfiltration.
/// </para>
/// <para>
/// <b><see cref="CanWrite"/> is a member rather than an exception to catch.</b> A deployment whose secrets
/// arrive from outside — configuration, a mounted file, a cloud vault through a configuration provider — has
/// no writable store at all, and a screen has to say so <em>before</em> an operator types a key into a box
/// that will refuse it.
/// </para>
/// <para>
/// <b>What this port deliberately does not have</b>, and each is tracked rather than forgotten: rotation with
/// key overlap, secret versioning, per-tenant isolation, and an access audit. Every one of them exists in the
/// analysis (§7.1) for a consumer this build does not have — JWT signing keys and webhook HMAC — so they are
/// designed when there is something to rotate rather than now.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>
    /// Gets a value indicating whether this store accepts writes.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> for a store backed by configuration, and for a deployment that registered no
    /// writable store at all. A caller that offers a control for setting a secret asks this first.
    /// </remarks>
    bool CanWrite { get; }

    /// <summary>Reads one secret.</summary>
    /// <param name="name">The name it is stored under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when nothing is stored under that name.</returns>
    /// <remarks>
    /// An unknown name is absence, not an error: asking whether a secret exists is an ordinary question, and
    /// a store that threw would make every caller wrap it.
    /// </remarks>
    ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default);

    /// <summary>Stores one secret, replacing whatever was there.</summary>
    /// <param name="name">The name to store it under.</param>
    /// <param name="value">The value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">This store does not accept writes.</exception>
    /// <exception cref="SecretShadowedException">
    /// A layer this store reads before its own already carries that name, so the write would be stored and
    /// never read.
    /// </exception>
    ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default);

    /// <summary>Removes one secret.</summary>
    /// <param name="name">The name to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    /// <remarks>
    /// It reports what it did so a caller need not read first — and a read before a delete is a value pulled
    /// out of the store for no reason.
    /// </remarks>
    ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default);

    /// <summary>Lists the names this store holds, without their values.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The names, in no guaranteed order.</returns>
    ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default);
}

/// <summary>
/// Marks the one registered <see cref="ISecretStore"/> a deployment can write to.
/// </summary>
/// <remarks>
/// The core registers the layered store under <see cref="ISecretStore"/> and resolves the writable half
/// through this, so a driver can register its own without racing that registration — the same shape the data
/// layer already uses for its optional ports.
/// </remarks>
public interface IWritableSecretStore : ISecretStore;
