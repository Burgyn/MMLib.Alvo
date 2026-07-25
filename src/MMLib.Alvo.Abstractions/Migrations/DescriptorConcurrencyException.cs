namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when an append loses the optimistic-lock race: the caller's expected revision no longer
/// matches the store's current revision, so another client changed the descriptor first.
/// </summary>
public sealed class DescriptorConcurrencyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DescriptorConcurrencyException"/> class.</summary>
    public DescriptorConcurrencyException()
    {
        Project = "";
        ExpectedRevision = 0;
        ActualRevision = 0;
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorConcurrencyException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public DescriptorConcurrencyException(string message)
        : base(message)
    {
        Project = "";
        ExpectedRevision = 0;
        ActualRevision = 0;
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorConcurrencyException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DescriptorConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
        Project = "";
        ExpectedRevision = 0;
        ActualRevision = 0;
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorConcurrencyException"/> class from a conflicting append.</summary>
    /// <param name="project">The project whose append conflicted.</param>
    /// <param name="expectedRevision">The revision the caller expected to be current.</param>
    /// <param name="actualRevision">The revision that was actually current.</param>
    public DescriptorConcurrencyException(string project, int expectedRevision, int actualRevision)
        : base($"Descriptor for project '{project}' changed concurrently: expected revision {expectedRevision}, but current is {actualRevision}. Reload the latest revision and retry.")
    {
        Project = project;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>Gets the project whose append conflicted.</summary>
    public string Project { get; }

    /// <summary>Gets the revision the caller expected to be current.</summary>
    public int ExpectedRevision { get; }

    /// <summary>Gets the revision that was actually current.</summary>
    public int ActualRevision { get; }
}
