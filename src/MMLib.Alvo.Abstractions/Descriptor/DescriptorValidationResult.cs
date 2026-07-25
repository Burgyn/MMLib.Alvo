namespace MMLib.Alvo.Descriptor;

/// <summary>Severity of a single descriptor validation finding.</summary>
public enum DescriptorValidationSeverity
{
    /// <summary>A blocking problem: the descriptor must not be applied.</summary>
    Error,

    /// <summary>A non-blocking advisory.</summary>
    Warning,
}

/// <summary>A single descriptor validation finding, agent-first: a JSON path, a message, and a fix suggestion.</summary>
/// <param name="Path">JSON pointer / path to the offending node (e.g. <c>/entities/invoices/fields/gross</c>).</param>
/// <param name="Message">What is wrong.</param>
/// <param name="FixSuggestion">How to fix it, if known.</param>
/// <param name="Severity">Whether this blocks apply.</param>
public sealed record DescriptorValidationError(
    string Path, string Message, string? FixSuggestion, DescriptorValidationSeverity Severity);

/// <summary>The outcome of validating a descriptor.</summary>
/// <param name="Errors">All findings, in document order.</param>
public sealed record DescriptorValidationResult(IReadOnlyList<DescriptorValidationError> Errors)
{
    /// <summary>Gets a value indicating whether the descriptor may be applied (no <see cref="DescriptorValidationSeverity.Error"/>).</summary>
    public bool IsValid => Errors.All(e => e.Severity != DescriptorValidationSeverity.Error);

    /// <summary>An empty, valid result.</summary>
    public static DescriptorValidationResult Valid { get; } = new([]);
}

/// <summary>Thrown when a descriptor fails validation before being applied.</summary>
public sealed class DescriptorValidationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DescriptorValidationException"/> class.</summary>
    public DescriptorValidationException()
        : this(DescriptorValidationResult.Valid)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorValidationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public DescriptorValidationException(string message)
        : base(message)
    {
        Result = DescriptorValidationResult.Valid;
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorValidationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DescriptorValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Result = DescriptorValidationResult.Valid;
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorValidationException"/> class from a failed validation result.</summary>
    /// <param name="result">The validation result whose errors caused this exception.</param>
    public DescriptorValidationException(DescriptorValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>Gets the validation result whose errors caused this exception.</summary>
    public DescriptorValidationResult Result { get; }

    private static string BuildMessage(DescriptorValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = result.Errors
            .Where(e => e.Severity == DescriptorValidationSeverity.Error)
            .Select(e => $"  {e.Path}: {e.Message}{(e.FixSuggestion is null ? "" : $" — {e.FixSuggestion}")}");
        return "Descriptor validation failed:\n" + string.Join("\n", lines);
    }
}
