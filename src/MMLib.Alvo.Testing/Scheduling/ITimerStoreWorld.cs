using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Testing.Scheduling;

/// <summary>An <see cref="ITimerStore"/> over live storage, plus what its own surface cannot do: move time, read and break the outbox.</summary>
public interface ITimerStoreWorld : IAsyncDisposable
{
    /// <summary>Gets the store under test, over storage that already exists.</summary>
    ITimerStore Store { get; }

    /// <summary>Gets the store's current instant, in UTC.</summary>
    DateTimeOffset Now { get; }

    /// <summary>Moves the store's clock forward by <paramref name="duration"/>.</summary>
    /// <param name="duration">How far time moves.</param>
    void Advance(TimeSpan duration);

    /// <summary>The types of every event appended to the outbox, in id order.</summary>
    /// <returns>The types.</returns>
    Task<IReadOnlyList<string>> OutboxTypesAsync();

    /// <summary>Drops the outbox, so the store's next append fails.</summary>
    /// <returns>A task that completes when the outbox is gone.</returns>
    Task BreakOutboxAsync();

    /// <summary>Every started data migration's row, by id.</summary>
    /// <returns>The id and revision of each.</returns>
    Task<IReadOnlyList<(string Id, int Revision)>> MigrationRowsAsync();
}
