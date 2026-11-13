namespace MMLib.Alvo.Auth.Internal;

/// <summary>Which of the two credential readings of spec §5.7 a site applies.</summary>
internal enum CredentialSurface
{
    /// <summary>The Data API, the command gate and every <c>.WithAlvoCaller()</c> endpoint: an API key, or a person's Bearer token.</summary>
    Data,

    /// <summary>The Management API, which reads the API key and never <c>Authorization</c>.</summary>
    Management,
}

/// <summary>What a request presented, once spec §5.7's table has been applied to it.</summary>
internal enum CredentialChannel
{
    /// <summary>No credential Alvo reads: the caller is anonymous.</summary>
    None,

    /// <summary>The API-key header.</summary>
    ApiKey,

    /// <summary>One well-formed <c>Authorization: Bearer</c> token, for the registered person resolver.</summary>
    Bearer,

    /// <summary>An API key and a Bearer token Alvo owns, together — refused, never resolved by precedence.</summary>
    Ambiguous,

    /// <summary>A Bearer token Alvo owns that is empty, carries whitespace, or arrives beside another <c>Authorization</c> line.</summary>
    MalformedBearer,
}

/// <summary>The one credential a request presented, as <see cref="CallerResolution.Read"/> read it.</summary>
/// <param name="Channel">Which channel the credential arrived on.</param>
/// <param name="Value">
/// The API key, or the bare Bearer token without its scheme; <see langword="null"/> for every other channel.
/// </param>
internal readonly record struct PresentedCredential(CredentialChannel Channel, string? Value)
{
    /// <summary>The reading of a request that presented nothing Alvo reads.</summary>
    internal static PresentedCredential Nothing => new(CredentialChannel.None, null);

    /// <inheritdoc/>
    /// <remarks>The value is a secret and never leaves through a log line; only the channel does.</remarks>
    public override string ToString() => $"{nameof(PresentedCredential)} {{ Channel = {Channel} }}";
}
