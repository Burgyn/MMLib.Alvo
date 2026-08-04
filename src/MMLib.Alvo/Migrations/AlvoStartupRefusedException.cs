namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown out of the boot sequence when Alvo will not start: the descriptor has drifted from the schema
/// applied to this database and the mode does not allow applying it, or the plan would discard data without
/// <see cref="AlvoSchemaOptions.AllowDestructive"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refusing to start is the designed behaviour, and the presentation is the part that matters.</b> The
/// throw happens in the host's <c>StartingAsync</c>, before the server binds, so nothing ever answers on a
/// process whose schema was not accepted. What this type adds over any other exception is that
/// <see cref="Exception.Message"/> is written for the operator who reads a container log — the headline, the
/// steps that were refused, and what to do about them — rather than for a stack trace.
/// </para>
/// <para>
/// <see cref="FixSuggestion"/> repeats the actionable half of <see cref="Exception.Message"/> as a separate
/// member on purpose. The message has to stand alone, because printing it is all a crashing process gets to
/// do; the property exists so a caller that presents failures structurally — the Management API, a dashboard,
/// an agent — reaches the fix without parsing prose.
/// </para>
/// </remarks>
public sealed class AlvoStartupRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AlvoStartupRefusedException"/> class.</summary>
    public AlvoStartupRefusedException() => FixSuggestion = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="AlvoStartupRefusedException"/> class.</summary>
    /// <param name="message">The operator-readable refusal.</param>
    public AlvoStartupRefusedException(string message)
        : base(message) => FixSuggestion = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="AlvoStartupRefusedException"/> class.</summary>
    /// <param name="message">The operator-readable refusal.</param>
    /// <param name="innerException">The failure that caused the refusal.</param>
    public AlvoStartupRefusedException(string message, Exception innerException)
        : base(message, innerException) => FixSuggestion = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlvoStartupRefusedException"/> class from a refusal and
    /// the fix it already contains.
    /// </summary>
    /// <param name="message">The operator-readable refusal, complete enough to print on its own.</param>
    /// <param name="fixSuggestion">The actionable half of <paramref name="message"/>, on its own.</param>
    public AlvoStartupRefusedException(string message, string fixSuggestion)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(fixSuggestion);

        FixSuggestion = fixSuggestion;
    }

    /// <summary>
    /// Gets what an operator has to change for this boot to succeed — the configuration keys, spelled as
    /// environment variables, and the alternative to setting them. Empty when the refusal carried no fix.
    /// </summary>
    public string FixSuggestion { get; }
}
