using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Threading;
using Xunit;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>Behavioral contract every <see cref="IDescriptorVersionStore"/> must satisfy — fake and real alike.</summary>
public abstract class DescriptorVersionStoreContractTests
{
    /// <summary>Creates the store under test.</summary>
    protected abstract IDescriptorVersionStore CreateStore();

    /// <summary>No-op unless the engine must be skipped in this environment.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private static DescriptorVersion Candidate(string json = "{}") =>
        new(new SchemaModel([]), json, Revision: 0, CreatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>The first ever append (expectedRevision 0 against an empty history) must land as revision 1.</summary>
    [Fact]
    public async Task First_append_is_revision_1()
    {
        EnsureEngineAvailable();
        var store = CreateStore();

        var appended = await store.AppendAsync("p", Candidate(), expectedRevision: 0);

        appended.Revision.ShouldBe(1);
        (await store.GetCurrentAsync("p"))!.Revision.ShouldBe(1);
    }

    /// <summary>Successive appends must accumulate in revision order rather than overwrite each other.</summary>
    [Fact]
    public async Task History_is_append_only_and_ordered()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate("{\"v\":1}"), 0);
        await store.AppendAsync("p", Candidate("{\"v\":2}"), 1);

        var history = await store.ListAsync("p");

        history.Select(v => v.Revision).ShouldBe([1, 2]);
    }

    /// <summary>An append with a stale <c>expectedRevision</c> must throw <see cref="DescriptorConcurrencyException"/> instead of silently applying.</summary>
    [Fact]
    public async Task Stale_expected_revision_conflicts()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate(), 0);

        var ex = await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => store.AppendAsync("p", Candidate(), expectedRevision: 0));

        ex.ExpectedRevision.ShouldBe(0);
        ex.ActualRevision.ShouldBe(1);
    }

    /// <summary><see cref="IDescriptorVersionStore.GetAsync"/> must return a past revision, not only the current one.</summary>
    [Fact]
    public async Task Get_returns_a_specific_historical_revision()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate("{\"v\":1}"), 0);
        await store.AppendAsync("p", Candidate("{\"v\":2}"), 1);

        (await store.GetAsync("p", 1))!.DescriptorJson.ShouldContain("\"v\":1");
    }

    /// <summary>Unknown project/revision lookups must return <see langword="null"/>, never throw.</summary>
    [Fact]
    public async Task Unknown_project_or_revision_returns_null()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate(), 0);

        (await store.GetCurrentAsync("unknown-project")).ShouldBeNull();
        (await store.GetAsync("p", revision: 99)).ShouldBeNull();
    }

    /// <summary>
    /// Two callers racing the SAME <c>expectedRevision</c> under GENUINE concurrency — not merely
    /// sequential calls — must yield exactly one winner. The loser must surface as
    /// <see cref="DescriptorConcurrencyException"/>, never a raw provider-specific error (e.g. a
    /// unique-constraint <c>DbException</c> escaping an EF-backed store) and the two must never both
    /// succeed. A <see cref="Barrier"/> releases both appends at (as close to) the same instant, and a
    /// warm-up call before the race forces any one-time setup (e.g. lazy table creation) to happen
    /// beforehand so it cannot accidentally serialize the two attempts.
    /// </summary>
    [Fact]
    public async Task Concurrent_appends_at_same_expected_revision_yield_exactly_one_winner()
    {
        EnsureEngineAvailable();
        var store = CreateStore();

        // Warm up any one-time, store-instance-wide setup (e.g. lazy CREATE TABLE) so it cannot
        // accidentally serialize the two racing appends below.
        await store.ListAsync("p");

        using var barrier = new Barrier(2);
        var first = Task.Run(() => RaceAppendAsync(store, barrier));
        var second = Task.Run(() => RaceAppendAsync(store, barrier));

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Conflict is null).ShouldBe(1);
        outcomes.Count(o => o.Conflict is not null).ShouldBe(1);

        var history = await store.ListAsync("p");
        history.Count.ShouldBe(1);
        history[0].Revision.ShouldBe(1);
    }

    private static async Task<(DescriptorVersion? Version, DescriptorConcurrencyException? Conflict)> RaceAppendAsync(
        IDescriptorVersionStore store, Barrier barrier)
    {
        barrier.SignalAndWait();
        try
        {
            return (await store.AppendAsync("p", Candidate(), expectedRevision: 0), null);
        }
        catch (DescriptorConcurrencyException ex)
        {
            return (null, ex);
        }
    }
}
