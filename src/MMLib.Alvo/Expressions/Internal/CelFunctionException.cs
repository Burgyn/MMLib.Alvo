namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A catalogued CEL function failed while an expression was evaluated. Deliberately <b>not</b> collapsed into a value
/// the way every other evaluation surprise is (false, null, masked): a function is the one construct whose failure is
/// reachable, so it fails closed — the interpreter lets it escape and the write rolls back. An in-process <c>IAlvoData</c>
/// caller receives it as a plain <see cref="Exception"/>; an after-hook condition drops the hook with a Warning. Internal:
/// only the core throws it and names it — no public signature exposes the type. A Data API caller receives
/// <c>…/errors/expression-failed</c> (422) when <see cref="OnCallerData"/> holds and <c>…/errors/function-failed</c> (500)
/// otherwise.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> always reads <c>The CEL function '…' failed…</c>, also for a whole hook condition that
/// evaluated to no Bool, whose name is <c>&lt;condition&gt;</c> (<see cref="CelInterpreter.WholeCondition"/>). An embedded
/// <c>IAlvoData</c> caller sees that wording as is; only the Data API's problem document rewords it, in
/// <c>AlvoExceptionHandler.FunctionFailedDetail</c>.
/// </remarks>
#pragma warning disable RCS1194 // Deliberately no standard constructors: a function failure always names its function.
internal sealed class CelFunctionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CelFunctionException"/> class for a body that threw.</summary>
    /// <param name="functionName">The function's name, which the problem document may show (descriptor-authored).</param>
    /// <param name="isHost">Whether the host registered it.</param>
    /// <param name="failure">What the body threw; kept for the log, never shown to the caller.</param>
    internal CelFunctionException(string functionName, bool isHost, Exception failure)
        : base($"The CEL function '{functionName}' failed.", failure)
    {
        FunctionName = functionName;
        IsHost = isHost;
    }

    /// <summary>Initializes a new instance of the <see cref="CelFunctionException"/> class for a built-in that refused.</summary>
    /// <param name="functionName">The built-in's name.</param>
    /// <param name="reason">Why, in Alvo's own words — safe to show the caller.</param>
    internal CelFunctionException(string functionName, string reason)
        : this(functionName, isHost: false, reason)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CelFunctionException"/> class for a call Alvo refused.</summary>
    /// <param name="functionName">The function's name.</param>
    /// <param name="isHost">Whether the host registered it.</param>
    /// <param name="reason">Why, in Alvo's own words — safe to show the caller, so it never carries a row's value.</param>
    internal CelFunctionException(string functionName, bool isHost, string reason)
        : this(functionName, isHost, reason, failure: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CelFunctionException"/> class for a call Alvo refused after the body
    /// threw — a host function past its budget: Alvo's reason for the caller, what the body threw for the log.
    /// </summary>
    /// <param name="functionName">The function's name.</param>
    /// <param name="isHost">Whether the host registered it.</param>
    /// <param name="reason">Why, in Alvo's own words — safe to show the caller, so it never carries a row's value.</param>
    /// <param name="failure">What the body threw, if anything; kept for the log, never shown to the caller.</param>
    internal CelFunctionException(string functionName, bool isHost, string reason, Exception? failure)
        : base($"The CEL function '{functionName}' failed: {reason}.", failure)
    {
        FunctionName = functionName;
        IsHost = isHost;
        Reason = reason;
        OnCallerData = !isHost && failure is null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CelFunctionException"/> class for a defect the interpreter caught on
    /// the fail-closed path (Ruling Y-D): Alvo's own reason for the caller, the original exception for the log.
    /// </summary>
    /// <param name="functionName">The token the defect is reported under.</param>
    /// <param name="reason">Why, in Alvo's own words — safe to show the caller, so it never carries a row's value.</param>
    /// <param name="failure">What was thrown; kept for the log, never shown to the caller.</param>
    internal CelFunctionException(string functionName, string reason, Exception failure)
        : base($"The CEL function '{functionName}' failed: {reason}.", failure)
    {
        FunctionName = functionName;
        Reason = reason;
    }

    private CelFunctionException(CelFunctionException original, bool onCallerData)
        : base(original.Message, original.InnerException)
    {
        FunctionName = original.FunctionName;
        IsHost = original.IsHost;
        Reason = original.Reason;
        OnCallerData = onCallerData;
        Judged = true;
    }

    /// <summary>Gets the failed function's name.</summary>
    public string FunctionName { get; }

    /// <summary>Gets a value indicating whether the host registered the function.</summary>
    public bool IsHost { get; }

    /// <summary>Gets Alvo's own reason for a built-in's refusal, or <see langword="null"/> when a body threw.</summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the refused value is the caller's own — a built-in, hook arithmetic or the interpreter
    /// refused what the request carries — so the caller fixes it by sending other data (422). Never for anything a host
    /// function did or produced, the stored row, a value a hook wrote, nor a defect.
    /// </summary>
    public bool OnCallerData { get; }

    /// <summary>Gets a value indicating whether a node has already decided whose data this is.</summary>
    internal bool Judged { get; }

    /// <summary>The failure as the innermost node that saw it attributes it; an already judged failure is returned as is.</summary>
    /// <param name="callersOwn">Whether that node's subtree reads only the caller's data.</param>
    /// <returns>The attributed failure.</returns>
    internal CelFunctionException JudgedBy(bool callersOwn) => Judged || !OnCallerData ? this : new(this, callersOwn);

    /// <summary>The failure, attributed as not the caller's (a value a hook wrote).</summary>
    /// <returns>A copy with <see cref="OnCallerData"/> false.</returns>
    internal CelFunctionException NotTheCallers() => OnCallerData ? new(this, onCallerData: false) : this;
}
#pragma warning restore RCS1194
