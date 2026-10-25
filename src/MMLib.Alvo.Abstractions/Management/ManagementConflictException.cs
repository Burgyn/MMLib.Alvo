namespace MMLib.Alvo.Management;

/// <summary>
/// The operation is well-formed and cannot be applied to what it names in the state it is in — a delivery that is running,
/// an extension of a kind that cannot be switched off. The message says what to do instead.
/// </summary>
public sealed class ManagementConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    public ManagementConflictException()
        : base("The operation cannot be applied in the state its target is in.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    /// <param name="message">What stands in the way, and what to do instead.</param>
    public ManagementConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    /// <param name="message">What stands in the way, and what to do instead.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
