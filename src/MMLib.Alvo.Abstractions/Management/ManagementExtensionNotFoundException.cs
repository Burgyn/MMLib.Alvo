namespace MMLib.Alvo.Management;

/// <summary>This host registers no extension by that id.</summary>
public sealed class ManagementExtensionNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementExtensionNotFoundException"/> class.</summary>
    public ManagementExtensionNotFoundException()
        : base("This host registers no extension by that name.")
    {
        Extension = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementExtensionNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementExtensionNotFoundException(string message)
        : base(message)
    {
        Extension = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementExtensionNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementExtensionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Extension = string.Empty;
    }

    /// <summary>Initializes a new instance naming the extension that was asked for and the ones that exist.</summary>
    /// <param name="extension">The id asked for.</param>
    /// <param name="registered">The ids this host registers.</param>
    public ManagementExtensionNotFoundException(string extension, IReadOnlyList<string> registered)
        : base($"This host registers no extension '{extension}'. It registers: {Listed(registered)}.")
    {
        Extension = extension;
    }

    /// <summary>Gets the id asked for.</summary>
    public string Extension { get; }

    private static string Listed(IReadOnlyList<string>? registered) =>
        registered is { Count: > 0 } ? string.Join(", ", registered) : "none";
}
