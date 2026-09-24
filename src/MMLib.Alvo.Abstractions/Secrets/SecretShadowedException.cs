namespace MMLib.Alvo.Secrets;

/// <summary>
/// A write refused because a layer the store reads first already carries that name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one failure mode layering a store over configuration can produce</b>, and the silent version of it
/// is the worst kind: the operator saves a key, the screen says saved, and every request keeps using the old
/// one. It fails closed and it says which name.
/// </para>
/// <para>
/// A caller catching this has one thing to tell the operator — that this name is pinned by the deployment's
/// own configuration, so it is changed there rather than here.
/// </para>
/// </remarks>
public sealed class SecretShadowedException : InvalidOperationException
{
    /// <summary>Initializes the refusal for one name.</summary>
    /// <param name="name">The name configuration already carries.</param>
    /// <param name="source">Where the shadowing value comes from, in words an operator can act on.</param>
    public SecretShadowedException(SecretName name, string source)
        : base($"Secret '{name?.Value}' is set by {source}, which is read before the writable store — so "
            + "storing a value under that name here would never be read. Change it where it is set, or "
            + "remove it from there first.")
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>Initializes the refusal with a message of the caller's own.</summary>
    /// <param name="message">The message.</param>
    public SecretShadowedException(string message)
        : base(message) => Name = SecretName.Parse("unknown");

    /// <summary>Initializes the refusal with a message and the failure under it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure under it.</param>
    public SecretShadowedException(string message, Exception innerException)
        : base(message, innerException) => Name = SecretName.Parse("unknown");

    /// <summary>Initializes an empty refusal.</summary>
    public SecretShadowedException()
        : base() => Name = SecretName.Parse("unknown");

    /// <summary>Gets the name whose write was refused.</summary>
    public SecretName Name { get; }
}
