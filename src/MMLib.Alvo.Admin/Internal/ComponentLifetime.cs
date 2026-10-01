using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// One component's lifetime as a cancellation token, ended when the component is disposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a component needs one</b> (docs/architecture/admin-dashboard-review.md, F-17): a screen that is
/// navigated away from mid-read used to let the read finish for nobody. Passing this token instead of
/// <see cref="CancellationToken.None"/> stops the work, and it is also what
/// <see cref="AdminSession.Follow(WorkingCopy, Func{Func{Task}, Task}, Func{Task}, CancellationToken)"/> reads
/// to know the component is gone.
/// </para>
/// <para>
/// <b>The token is held, not read from the source each time.</b> An await that resumes after the component
/// was disposed still reads <see cref="Token"/>, and a disposed source throws from its own <c>Token</c>; a
/// token taken at construction answers "cancelled" instead, which is the truth.
/// </para>
/// <para>
/// <b>How a cancellation reads to the error policy.</b> An <see cref="OperationCanceledException"/> whose
/// token is this <see cref="Token"/> — or that arrives once <see cref="Ended"/> — is the operator leaving;
/// any other is something else timing out. <see cref="AdminProblem"/> drops both today, and this is the
/// test it can apply when it stops doing so.
/// </para>
/// </remarks>
internal sealed class ComponentLifetime : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>Creates a lifetime that has not ended.</summary>
    public ComponentLifetime() => Token = _source.Token;

    /// <summary>Cancelled when the component is disposed.</summary>
    public CancellationToken Token { get; }

    /// <summary>Whether the component has been disposed.</summary>
    public bool Ended => Token.IsCancellationRequested;

    /// <summary>Ends the lifetime: cancels what is in flight and unsubscribes what followed it.</summary>
    public void Dispose()
    {
        if (!_source.IsCancellationRequested)
        {
            _source.Cancel();
        }

        _source.Dispose();
    }
}
