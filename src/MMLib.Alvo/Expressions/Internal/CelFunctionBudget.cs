using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// One evaluation pass's time for host functions: every call inside it shares one deadline, whose clock starts at the
/// first call. Ambient (<see cref="AsyncLocal{T}"/>), because the interpreter is static and a host function's body is a
/// delegate in the compiled tree; the three places that evaluate host-callable CEL open it.
/// </summary>
/// <remarks>
/// A token-aware function receives a token cancelled at the deadline (linked with the caller's); every function is
/// measured when it returns, because .NET cannot stop a thread that ignores its token and the write must still fail. One
/// boundary for both: reaching the limit is over it. Once the deadline has passed, whatever the body threw — an
/// <see cref="OperationCanceledException"/>, an <see cref="AggregateException"/> from <c>.Result</c>, its own exception —
/// is the overrun. A function that never returns holds the evaluation until it does.
/// </remarks>
internal sealed class CelFunctionBudget : IDisposable
{
    private static readonly AsyncLocal<CelFunctionBudget?> _current = new();

    private readonly CelFunctionBudget? _previous;
    private readonly TimeProvider _time;
    private readonly CancellationToken _outer;
    private CancellationTokenSource? _deadline;
    private CancellationTokenSource? _linked;
    private long _startedAt;
    private bool _disposed;

    private CelFunctionBudget(TimeSpan limit, TimeProvider time, CancellationToken outer)
    {
        Limit = limit;
        _time = time;
        _outer = outer;
        _previous = _current.Value;
    }

    /// <summary>
    /// Gets the pass the current evaluation runs in, or <see langword="null"/> — also when the flow still holds a pass that
    /// was disposed (a task or timer started during the evaluation captured it): that evaluation is over, so a late call
    /// runs as outside any pass, and never touches the pass's disposed token sources.
    /// </summary>
    internal static CelFunctionBudget? Current => _current.Value is { _disposed: false } pass ? pass : null;

    /// <summary>Gets the time every host call of this pass shares.</summary>
    internal TimeSpan Limit { get; }

    private bool Overran =>
        _deadline is not null && (_deadline.IsCancellationRequested || _time.GetElapsedTime(_startedAt) >= Limit);

    /// <summary>Opens a pass; dispose it to restore the one around it.</summary>
    /// <param name="limit">The time host calls share.</param>
    /// <param name="time">The host's clock.</param>
    /// <param name="outer">The caller's token, linked into the one a function receives.</param>
    /// <returns>The pass.</returns>
    internal static CelFunctionBudget Enter(TimeSpan limit, TimeProvider time, CancellationToken outer)
    {
        ArgumentNullException.ThrowIfNull(time);
        var pass = new CelFunctionBudget(limit, time, outer);
        _current.Value = pass;
        return pass;
    }

    /// <summary>Calls a host function's body under the current pass, if any.</summary>
    /// <param name="function">The function's CEL name, for the overrun's message.</param>
    /// <param name="tokenAware">Whether the body takes a trailing token.</param>
    /// <param name="arguments">The CEL arguments, already converted.</param>
    /// <param name="body">The body.</param>
    /// <returns>What the body returned.</returns>
    /// <exception cref="CelFunctionException">The body ran past the pass's deadline.</exception>
    internal static object? Call(string function, bool tokenAware, object?[] arguments, Func<object?[], object?> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var pass = Current;
        var token = pass?.Start() ?? CancellationToken.None;
        object? result;
        try
        {
            result = body(tokenAware ? [.. arguments, token] : arguments);
        }
        catch (Exception thrown) when (pass is not null && pass.Overran)
        {
            throw pass.Overrun(function, thrown);
        }

        return pass is not null && pass.Overran ? throw pass.Overrun(function, failure: null) : result;
    }

    /// <summary>
    /// Leaves the pass: restores the one around it when this pass is still the flow's current one, so a second or an
    /// out-of-order dispose never restores a stale pass.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(_current.Value, this))
        {
            _current.Value = _previous;
        }

        _linked?.Dispose();
        _deadline?.Dispose();
    }

    private CancellationToken Start()
    {
        if (_linked is null)
        {
            _startedAt = _time.GetTimestamp();
            _deadline = new CancellationTokenSource(Limit, _time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_outer, _deadline.Token);
        }

        return _linked.Token;
    }

    private CelFunctionException Overrun(string function, Exception? failure) =>
        new(function, isHost: true,
            $"it ran past the {Limit.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms that host functions have in one evaluation",
            failure);
}
