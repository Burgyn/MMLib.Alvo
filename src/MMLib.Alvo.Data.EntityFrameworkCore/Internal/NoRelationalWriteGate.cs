namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The <see cref="IRelationalWriteGate"/> of an engine that runs concurrent writers itself: every caller enters at once.</summary>
internal sealed class NoRelationalWriteGate : IRelationalWriteGate
{
    private static readonly IDisposable _turn = new Turn();

    private NoRelationalWriteGate()
    {
    }

    /// <summary>Gets the one instance; it holds no state.</summary>
    public static NoRelationalWriteGate Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_turn);

    private sealed class Turn : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
