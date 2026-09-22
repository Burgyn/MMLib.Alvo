namespace MMLib.Alvo.Secrets;

/// <summary>
/// Thrown when a deployment is asked to save a secret it has no way to save.
/// </summary>
/// <remarks>
/// <para>
/// <b>An <see cref="InvalidOperationException"/>, deliberately.</b> That is what a store answering
/// <c>CanWrite == false</c> has always thrown, and the shared contract suite asserts it — so this narrows
/// the type without changing what any existing caller catches.
/// </para>
/// <para>
/// It exists so a transport can tell this refusal apart from a bug. "This deployment's secrets come from
/// configuration" is a sentence the caller can act on; an <see cref="InvalidOperationException"/> the
/// framework raised by mistake is not, and one catch for both would turn every such bug into a tidy 4xx.
/// </para>
/// </remarks>
public sealed class SecretStoreReadOnlyException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SecretStoreReadOnlyException"/> class.</summary>
    public SecretStoreReadOnlyException()
        : base("This deployment has no writable secret store.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreReadOnlyException"/> class.</summary>
    /// <param name="message">What to do instead, complete enough to print on its own.</param>
    public SecretStoreReadOnlyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreReadOnlyException"/> class.</summary>
    /// <param name="message">What to do instead.</param>
    /// <param name="innerException">What went wrong underneath.</param>
    public SecretStoreReadOnlyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
