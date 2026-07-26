namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown by <see cref="IAlvoData.UpdateAsync"/>/<see cref="IAlvoData.DeleteAsync"/> when the
/// targeted row does not exist, <b>or</b> exists but the caller's policy <c>USING</c> predicate
/// excludes it. The two cases are deliberately indistinguishable: a row a caller may not see must
/// read exactly like a row that was never there, so this exception can never become an oracle
/// that reveals another tenant's or another user's data exists.
/// </summary>
/// <remarks>
/// The message never names the entity or the row id — a caller-supplied id echoed back would
/// itself be harmless, but the entity/id pairing is exactly what a probing attacker wants
/// confirmed or denied, so neither appears here at all.
/// </remarks>
public sealed class AlvoRecordNotFoundException : Exception
{
    private const string DefaultMessage = "The requested record was not found.";

    /// <summary>Initializes a new instance of the <see cref="AlvoRecordNotFoundException"/> class.</summary>
    public AlvoRecordNotFoundException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoRecordNotFoundException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoRecordNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoRecordNotFoundException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AlvoRecordNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
