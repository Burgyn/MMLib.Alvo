namespace MMLib.Alvo.Functions;

/// <summary>
/// Thrown by a function that decides not to act on this event: the delivery ends succeeded, logged as skipped, and is not
/// retried.
/// </summary>
public sealed class AlvoFunctionSkippedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AlvoFunctionSkippedException"/> class.</summary>
    public AlvoFunctionSkippedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoFunctionSkippedException"/> class with why it skipped.</summary>
    /// <param name="message">Why the function did not act.</param>
    public AlvoFunctionSkippedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoFunctionSkippedException"/> class with why and its cause.</summary>
    /// <param name="message">Why the function did not act.</param>
    /// <param name="innerException">The exception that led to skipping.</param>
    public AlvoFunctionSkippedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
