using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Threading;
using Xunit;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// Behavioral contract every <see cref="IRuntimeSchemaWriter"/> must satisfy — fake and real alike.
/// </summary>
/// <remarks>
/// A real plan's SQL is provider-specific, so this suite exercises the <em>conflict + append</em>
/// semantics with an empty-SQL plan; the atomic DDL-plus-append guarantee (that a lost race rolls
/// back the DDL too) is proven by the provider's own integration test, where real DDL is available.
/// </remarks>
public abstract class RuntimeSchemaWriterContractTests
{
    /// <summary>Creates the writer under test. Successive calls within one test must share backing state.</summary>
    protected abstract IRuntimeSchemaWriter CreateWriter();

    /// <summary>No-op unless the engine must be skipped in this environment.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private static MigrationPlan EmptyPlan => new() { Steps = [], Sql = [] };

    private static DescriptorVersion Candidate(string json = "{}") =>
        new(new SchemaModel([]), json, Revision: 0, CreatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>The first apply lands as revision 1; a second apply at the same stale expected revision conflicts.</summary>
    [Fact]
    public async Task Winner_appends_loser_conflicts()
    {
        EnsureEngineAvailable();
        var writer = CreateWriter();

        var appended = await writer.ApplyAndAppendAsync("p", EmptyPlan, Candidate(), expectedRevision: 0, new MigrationOptions());
        appended.Revision.ShouldBe(1);

        var ex = await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => writer.ApplyAndAppendAsync("p", EmptyPlan, Candidate(), expectedRevision: 0, new MigrationOptions()));

        ex.ExpectedRevision.ShouldBe(0);
        ex.ActualRevision.ShouldBe(1);
    }

    /// <summary>
    /// Two callers racing the SAME expected revision under GENUINE concurrency must yield exactly one
    /// winner; the loser must surface as <see cref="DescriptorConcurrencyException"/>, never a raw
    /// provider error, and the two must never both append. This is the atomicity gate: the version-row
    /// optimistic-lock insert rejects the loser before it can commit anything.
    /// </summary>
    [Fact]
    public async Task Concurrent_applies_at_same_expected_revision_yield_exactly_one_winner()
    {
        EnsureEngineAvailable();
        var writer = CreateWriter();

        // Warm up any one-time, instance-wide setup (e.g. lazy CREATE TABLE) so it cannot
        // accidentally serialize the two racing applies below.
        await writer.ApplyAndAppendAsync("warmup", EmptyPlan, Candidate(), expectedRevision: 0, new MigrationOptions());

        using var barrier = new Barrier(2);
        var first = Task.Run(() => RaceAsync(writer, barrier));
        var second = Task.Run(() => RaceAsync(writer, barrier));

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Conflict is null).ShouldBe(1);
        outcomes.Count(o => o.Conflict is not null).ShouldBe(1);
    }

    private static async Task<(DescriptorVersion? Version, DescriptorConcurrencyException? Conflict)> RaceAsync(
        IRuntimeSchemaWriter writer, Barrier barrier)
    {
        barrier.SignalAndWait();
        try
        {
            return (await writer.ApplyAndAppendAsync("p", EmptyPlan, Candidate(), expectedRevision: 0, new MigrationOptions()), null);
        }
        catch (DescriptorConcurrencyException ex)
        {
            return (null, ex);
        }
    }
}
