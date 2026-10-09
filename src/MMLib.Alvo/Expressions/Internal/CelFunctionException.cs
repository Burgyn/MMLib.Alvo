namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A catalogued CEL function failed while an expression was evaluated. Deliberately <b>not</b> collapsed into a value
/// the way every other evaluation surprise is (false, null, masked): a function is the one construct whose failure is
/// reachable, so it fails closed — the interpreter lets it escape and the write rolls back. A Data API caller receives
/// <c>…/errors/function-failed</c> (spec §5.6); an in-process <c>IAlvoData</c> caller receives it as a plain
/// <see cref="Exception"/>; an after-hook condition drops the hook with a Warning. Internal: only the core throws it and
/// names it — no public signature exposes the type.
/// </summary>
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
        : base($"The CEL function '{functionName}' failed: {reason}.")
    {
        FunctionName = functionName;
        IsHost = isHost;
        Reason = reason;
    }

    /// <summary>Gets the failed function's name.</summary>
    public string FunctionName { get; }

    /// <summary>Gets a value indicating whether the host registered the function.</summary>
    public bool IsHost { get; }

    /// <summary>Gets Alvo's own reason for a built-in's refusal, or <see langword="null"/> when a body threw.</summary>
    public string? Reason { get; }
}
#pragma warning restore RCS1194
