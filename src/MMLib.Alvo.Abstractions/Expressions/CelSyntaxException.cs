namespace MMLib.Alvo.Expressions;

/// <summary>
/// Thrown when CEL source violates the grammar a profile allows — from the lexer, the parser, or
/// (in a later task) the type checker. Carries the character <see cref="Position"/> of the
/// offending token and, when one exists, a <see cref="FixSuggestion"/> phrased as a concrete
/// rewrite, so the compiler can turn a syntax error into a structured, actionable descriptor error
/// without losing the suggestion.
/// </summary>
public sealed class CelSyntaxException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CelSyntaxException"/> class.</summary>
    public CelSyntaxException()
    {
        Position = -1;
    }

    /// <summary>Initializes a new instance of the <see cref="CelSyntaxException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public CelSyntaxException(string message)
        : base(message)
    {
        Position = -1;
    }

    /// <summary>Initializes a new instance of the <see cref="CelSyntaxException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public CelSyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
        Position = -1;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CelSyntaxException"/> class for a specific
    /// offending position in the source.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="position">The zero-based character offset of the offending token in the source.</param>
    /// <param name="fixSuggestion">A concrete rewrite the caller can apply, when one is available.</param>
    public CelSyntaxException(string message, int position, string? fixSuggestion = null)
        : base(message)
    {
        Position = position;
        FixSuggestion = fixSuggestion;
    }

    /// <summary>
    /// Gets the zero-based character offset of the offending token in the source, or <c>-1</c>
    /// when no position is known (the parameterless/message-only constructors).
    /// </summary>
    public int Position { get; }

    /// <summary>Gets a concrete fix suggestion the caller can apply, when one is available.</summary>
    public string? FixSuggestion { get; }
}
