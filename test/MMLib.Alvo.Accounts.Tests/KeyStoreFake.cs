using System.Collections.Concurrent;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// An <see cref="IAccountStore"/> that holds keys in memory and counts every key call — the key half of the port, which
/// is all <c>AccountKeys</c> reaches. Hand-written rather than an NSubstitute substitute: the port is internal to
/// Abstractions, which grants no proxy generator.
/// </summary>
internal sealed class KeyStoreFake : IAccountStore
{
    private readonly ConcurrentDictionary<(string Purpose, int Generation), StoredKey> _keys = new();

    private int _ensures;

    private int _reads;

    /// <summary>Gets how many times <see cref="EnsureKeyAsync"/> was called.</summary>
    public int Ensures => Volatile.Read(ref _ensures);

    /// <summary>Gets how many times <see cref="ReadKeysAsync"/> was called.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Puts <paramref name="key"/> in the store as another replica (or a rotation) would.</summary>
    /// <param name="key">The key.</param>
    public void Add(StoredKey key) => _keys[(key.Purpose, key.Generation)] = key;

    /// <inheritdoc/>
    public Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct)
    {
        Interlocked.Increment(ref _ensures);
        var stored = new StoredKey(candidate.Purpose, candidate.Generation, candidate.Kid, candidate.Material, candidate.Now, null);
        return Task.FromResult(_keys.GetOrAdd((candidate.Purpose, candidate.Generation), stored));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        IReadOnlyList<StoredKey> live = [.. _keys.Values.Where(key => key.Purpose == purpose).OrderBy(key => key.Generation)];
        return Task.FromResult(live);
    }

    /// <inheritdoc/>
    public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<CallerRead> ReadCallerAsync(CallerQuery query, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct)
        => throw new NotSupportedException();
}
