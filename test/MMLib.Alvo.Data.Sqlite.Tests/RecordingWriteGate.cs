using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>Passes every turn through to a real gate, counting the turns and the most writers ever inside at once.</summary>
internal sealed class RecordingWriteGate(IRelationalWriteGate inner) : IRelationalWriteGate
{
    private int _entered;
    private int _inside;
    private int _most;

    /// <summary>Gets the gate every turn is passed through to.</summary>
    internal IRelationalWriteGate Inner => inner;

    /// <summary>Gets how many turns were taken.</summary>
    internal int Entered => Volatile.Read(ref _entered);

    /// <summary>Gets the most writers that were ever inside at once.</summary>
    internal int MostInside => Volatile.Read(ref _most);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var turn = await inner.EnterAsync(cancellationToken);
        Interlocked.Increment(ref _entered);
        RaiseTo(ref _most, Interlocked.Increment(ref _inside));

        return new Leaving(this, turn);
    }

    /// <summary>Raises <paramref name="target"/> to <paramref name="value"/> when it is lower, atomically.</summary>
    internal static void RaiseTo(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class Leaving(RecordingWriteGate gate, IDisposable turn) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref gate._inside);
            turn.Dispose();
        }
    }
}
