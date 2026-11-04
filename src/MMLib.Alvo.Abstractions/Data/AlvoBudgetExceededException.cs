namespace MMLib.Alvo.Data;

/// <summary>
/// A write hook made more calls through its transaction in one run than its operation budget allows. The write it runs in
/// is failed: catching this changes nothing, the transaction rolls back.
/// </summary>
public sealed class AlvoBudgetExceededException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    public AlvoBudgetExceededException()
        : base("A write hook made more calls through its transaction than its operation budget allows.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    public AlvoBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal for <paramref name="extension"/>'s call past <paramref name="limit"/>.</summary>
    /// <param name="extension">The hook's id.</param>
    /// <param name="limit">Its budget.</param>
    internal static AlvoBudgetExceededException For(string extension, int limit) => new(
        $"The write hook '{extension}' made more than {limit} calls through its transaction in one run, so the write is failed "
        + "and will roll back. Fix: write many rows with one batch call, or raise AlvoHookOptions.MaxOperations.");
}
