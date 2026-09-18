namespace MMLib.Alvo.Management;

/// <summary>This instance serves no project by that name.</summary>
/// <remarks>
/// Its own type rather than an <see cref="InvalidOperationException"/>, because the HTTP layer has nothing
/// but the exception type to map from, and family 5 — "an invariant Alvo relies on is broken" — propagates
/// as a 500, which is what an unknown project name would otherwise become.
/// </remarks>
public sealed class ManagementProjectNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    public ManagementProjectNotFoundException()
        : base("This instance serves no project by that name.")
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementProjectNotFoundException(string message)
        : base(message)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementProjectNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance naming the project that was asked for.</summary>
    /// <param name="project">The project name the caller asked for.</param>
    /// <param name="served">The projects this instance does serve.</param>
    public ManagementProjectNotFoundException(string project, IReadOnlyList<string> served)
        : base($"This instance serves no project '{project}'. It serves: {string.Join(", ", served ?? [])}.")
    {
        Project = project;
    }

    /// <summary>Gets the project name the caller asked for.</summary>
    public string Project { get; }
}
