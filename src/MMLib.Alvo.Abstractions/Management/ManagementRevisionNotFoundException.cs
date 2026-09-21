namespace MMLib.Alvo.Management;

/// <summary>That project has no such revision.</summary>
/// <remarks>
/// Its own type beside <see cref="ManagementProjectNotFoundException"/>, and for the same reason: the HTTP
/// layer has nothing but the exception type to map from. A shared "not found" carrying a discriminator would
/// put the decision in a string.
/// </remarks>
public sealed class ManagementRevisionNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementRevisionNotFoundException"/> class.</summary>
    public ManagementRevisionNotFoundException()
        : base("That project has no such revision.")
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementRevisionNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementRevisionNotFoundException(string message)
        : base(message)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementRevisionNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementRevisionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance naming the project and the revision that was asked for.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="revision">The revision number the caller asked for.</param>
    public ManagementRevisionNotFoundException(string project, int revision)
        : base($"Project '{project}' has no revision {revision}.")
    {
        Project = project;
        Revision = revision;
    }

    /// <summary>Gets the project name.</summary>
    public string Project { get; }

    /// <summary>Gets the revision number the caller asked for.</summary>
    public int Revision { get; }
}
