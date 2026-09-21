using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Management;

/// <summary>
/// The contract every <see cref="IManagementIdempotencyStore"/> implementation satisfies. Inherited by each
/// engine's suite, so a second driver cannot quietly implement a different replay rule.
/// </summary>
/// <remarks>
/// The same arrangement <see cref="Migrations.DescriptorVersionStoreContractTests"/> established: the facts
/// live once, beside the port, and every driver's own suite is the three lines that build a store.
/// </remarks>
public abstract class ManagementIdempotencyStoreContractTests
{
    /// <summary>Creates a store over a fresh, empty database.</summary>
    /// <returns>The store under test.</returns>
    protected abstract IManagementIdempotencyStore CreateStore();

    /// <summary>No-op unless the engine must be skipped in this environment.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A key nobody has spent is not a replay, and answering one would invent a revision.</summary>
    [Fact]
    public async Task A_key_nobody_has_used_is_not_a_replay()
    {
        EnsureEngineAvailable();
        var store = CreateStore();

        (await store.FindAsync("k1", "t/u", "fp")).ShouldBeNull();
    }

    /// <summary>A recorded key answers the revision it recorded, which is the whole point of recording it.</summary>
    [Fact]
    public async Task A_recorded_key_replays_the_revision_it_recorded()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.RecordAsync("k1", "t/u", "fp", revision: 7);

        (await store.FindAsync("k1", "t/u", "fp")).ShouldBe(7);
    }

    /// <summary>One caller's key never reaches another's record, because the scope is half the identity.</summary>
    [Fact]
    public async Task One_caller_s_key_never_reaches_another_s_record()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.RecordAsync("k1", "t/u", "fp", revision: 7);

        (await store.FindAsync("k1", "t/other", "fp")).ShouldBeNull();
    }

    /// <summary>
    /// A key reused for a different request is refused, never replayed: answering with the first request's
    /// revision would report success for an apply that never happened.
    /// </summary>
    [Fact]
    public async Task A_key_reused_for_a_different_request_is_refused_rather_than_replayed()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.RecordAsync("k1", "t/u", "fp", revision: 7);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(
            () => store.FindAsync("k1", "t/u", "a-different-request"));
    }

    /// <summary>
    /// Recording one key twice is refused by the store rather than silently overwritten — the primary key is
    /// the concurrency control, so two racing recorders cannot both file a revision under one key.
    /// </summary>
    [Fact]
    public async Task Recording_the_same_key_twice_is_refused_rather_than_silently_overwriting()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.RecordAsync("k1", "t/u", "fp", revision: 7);

        await Should.ThrowAsync<Exception>(() => store.RecordAsync("k1", "t/u", "fp", revision: 8));
        (await store.FindAsync("k1", "t/u", "fp")).ShouldBe(7, "the first record is the one that stands");
    }
}
