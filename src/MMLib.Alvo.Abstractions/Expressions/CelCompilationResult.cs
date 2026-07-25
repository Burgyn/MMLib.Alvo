namespace MMLib.Alvo.Expressions;

/// <summary>
/// A single problem found while compiling a CEL expression — a syntax error, a type error, an
/// out-of-profile construct, or a tree that nests too deeply. Carries the same shape a
/// <see cref="CelSyntaxException"/> does (a position and an optional, concrete fix) so an agent
/// can point at the source and apply the suggested rewrite regardless of which stage rejected it.
/// </summary>
/// <param name="Message">A human-readable description of the problem.</param>
/// <param name="FixSuggestion">A concrete rewrite that resolves the problem, when one is available.</param>
/// <param name="Position">The zero-based character offset in the source the problem is anchored to.</param>
public sealed record CelCompilationError(string Message, string? FixSuggestion, int Position);

/// <summary>
/// The outcome of <see cref="ICelCompiler.Compile"/>: either a <see cref="CompiledExpression"/> a
/// renderer can trust, or every problem that made the source unusable, so the caller can report
/// them all in one round trip rather than fixing one error at a time.
/// </summary>
public sealed record CelCompilationResult
{
    private CelCompilationResult(bool isSuccess, CompiledExpression? expression, IReadOnlyList<CelCompilationError> errors)
    {
        IsSuccess = isSuccess;
        Expression = expression;
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether compilation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the compiled expression when <see cref="IsSuccess"/> is <see langword="true"/>; otherwise <see langword="null"/>.</summary>
    public CompiledExpression? Expression { get; }

    /// <summary>Gets every problem found while compiling the source; empty when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<CelCompilationError> Errors { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="expression">The compiled expression.</param>
    public static CelCompilationResult Success(CompiledExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new CelCompilationResult(true, expression, []);
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="errors">Every problem found while compiling the source.</param>
    public static CelCompilationResult Failure(params CelCompilationError[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new CelCompilationResult(false, null, errors);
    }
}
