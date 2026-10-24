namespace MMLib.Alvo.Data;

/// <summary>
/// A lambda Alvo cannot translate: a typed handler's condition when the handler is registered, a typed query when it is
/// called. The message names the construct, the handler or entity, and why; <see cref="FixSuggestion"/> says what to
/// write instead.
/// </summary>
public sealed class AlvoTranslationException : Exception
{
    /// <summary>Initializes an exception with no message.</summary>
    public AlvoTranslationException() => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    public AlvoTranslationException(string message)
        : base(message) => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message and its cause.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoTranslationException(string message, Exception innerException)
        : base(message, innerException) => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message and what to write instead.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    /// <param name="fixSuggestion">What to write instead.</param>
    public AlvoTranslationException(string message, string fixSuggestion)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(fixSuggestion);

        FixSuggestion = fixSuggestion;
    }

    /// <summary>Gets what to write instead.</summary>
    public string FixSuggestion { get; }
}
