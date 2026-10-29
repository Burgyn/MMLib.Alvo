using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A top-level write's transaction lets go of the write turn even when disposing it throws — as the nested
/// <c>using</c> declarations it replaced did — so one failed rollback cannot stop every later write (Task 7 fix round 1, I1).
/// </summary>
public sealed class TurnHeldTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_transaction_whose_dispose_throws_still_releases_the_turn(bool asynchronously)
    {
        var gate = new SerialRelationalWriteGate();
        var held = new TurnHeldTransaction(new ThrowingTransaction(), await gate.EnterAsync(Ct));

        if (asynchronously)
        {
            await Should.ThrowAsync<DisposeFailure>(async () => await held.DisposeAsync());
        }
        else
        {
            Should.Throw<DisposeFailure>(held.Dispose);
        }

        using var next = await gate.TryEnterAsync(TimeSpan.Zero, Ct);
        next.ShouldNotBeNull("the turn was released although the transaction's dispose threw");
    }

    private sealed class DisposeFailure() : Exception("the rollback on dispose failed");

    private sealed class ThrowingTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit()
        {
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Rollback()
        {
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => throw new DisposeFailure();

        public ValueTask DisposeAsync() => throw new DisposeFailure();
    }
}
