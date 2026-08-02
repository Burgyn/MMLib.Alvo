using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Boot;

/// <summary>
/// What one replica's cold start ended as, plus the probe counts that make the claim non-vacuous.
/// </summary>
/// <param name="Replica">Which replica this was.</param>
/// <param name="Phase">The phase its <see cref="AlvoBootState"/> published.</param>
/// <param name="AppliedRevision">The applied revision it is serving, if any.</param>
/// <param name="Failure">What its <c>StartAsync</c> threw, or <see langword="null"/> when it started.</param>
/// <param name="AppliedSchemaReads">
/// How many times its boot read the applied snapshot. One means it never had to converge; two means it lost
/// the race, re-read, and re-decided — and anything above two would mean the retry is a loop.
/// </param>
/// <param name="SchemaWrites">
/// How many times its boot reached the schema write. Every replica in a genuine cold-start race reaches it
/// exactly once; a zero here would mean the replica decided something before the race even began.
/// </param>
/// <param name="AppliedSchemaWrites">How many of those writes actually applied. Exactly one replica's does.</param>
/// <param name="Rendezvoused">
/// Whether this replica met the others at the barrier in front of the schema write. False means the test did
/// not contend and proves nothing — the whole fact would be theatre.
/// </param>
/// <param name="Trace">
/// Every port call this replica's boot made, in order. A concurrency fact that fails is otherwise almost
/// unreadable: the exception it reports is the <em>last</em> thing that went wrong, and which attempt it
/// belonged to — and what the attempt before it saw — is the only thing that explains it.
/// </param>
internal sealed record ColdStartOutcome(
    int Replica,
    AlvoBootPhase Phase,
    int? AppliedRevision,
    Exception? Failure,
    int AppliedSchemaReads,
    int SchemaWrites,
    int AppliedSchemaWrites,
    bool Rendezvoused,
    IReadOnlyList<string> Trace)
{
    /// <summary>Whether this replica's host started, i.e. whether the boot let it serve.</summary>
    internal bool Serving => Failure is null;

    /// <summary>This replica's trace on one line, for a failing assertion's message.</summary>
    internal string Explain() => $"replica {Replica}: {string.Join(" | ", Trace)}";
}

/// <summary>Every replica's outcome, plus the descriptor history the race actually left behind.</summary>
/// <param name="Replicas">One entry per replica, in the order they were built.</param>
/// <param name="RecordedRevisions">
/// The revisions the descriptor-versions table holds afterwards. The whole point of the optimistic append is
/// that three replicas initializing one empty database leave <c>[1]</c>, not <c>[1, 2, 3]</c>.
/// </param>
internal sealed record ColdStartRace(
    IReadOnlyList<ColdStartOutcome> Replicas, IReadOnlyList<int> RecordedRevisions);

/// <summary>
/// N replicas of one embedded Alvo, each in its own container, cold-starting against <b>one</b> database at
/// the same instant — the ordinary first deployment of a replica set, and the scenario that either converges
/// or crash-loops.
/// </summary>
/// <remarks>
/// <para>
/// <b>The race is forced, not hoped for.</b> Starting three hosts with <c>Task.WhenAll</c> is not a race:
/// each one loads and validates a descriptor, compiles its rules and builds two EF models before it touches
/// the database, and those take long enough — and vary enough — that the three schema writes can miss each
/// other entirely. So a <see cref="Barrier"/> sits in a decorator in front of <see cref="ISchemaMigrator"/>'s
/// write, releasing every replica into it together, and <see cref="ColdStartOutcome.Rendezvoused"/> reports
/// whether they actually met. A fact that reads that flag cannot silently degrade into a sequential test.
/// </para>
/// <para>
/// <b>A plain generic host, not <c>WebApplication</c>.</b> Nothing here needs a server: the boot is an
/// <see cref="IHostedLifecycleService"/>, so <c>StartAsync</c> is the whole subject, and the routes that
/// would need a server do not materialise until the schema is primed anyway. The host is built
/// <em>empty</em> so no ambient <c>Alvo__Schema__*</c> environment variable can change what is measured.
/// </para>
/// <para>
/// Public surface only — <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c> is not granted the core's
/// internals, and one shared harness on both engines is the only way the two legs measure the same thing.
/// </para>
/// </remarks>
internal static class ConcurrentColdStart
{
    /// <summary>How long a replica waits for the others at the barrier before giving up on the race.</summary>
    private static readonly TimeSpan _rendezvousTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The project every descriptor below declares, so all replicas contend for one snapshot.</summary>
    internal const string Project = "cold-start";

    /// <summary>One entity, no drift — what an ordinary replica set deploys.</summary>
    internal const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "cold-start",
          "description": "One entity, deployed to several replicas at once.",
          "auth": { "providers": ["local"], "roles": ["admin"] },
          "entities": {
            "depots": {
              "description": "A depot.",
              "fields": {
                "code": { "type": "string", "required": true, "unique": true, "maxLength": 20 }
              },
              "rules": {
                "list": "'authenticated' in @user.roles",
                "get": "'authenticated' in @user.roles",
                "create": "'admin' in @user.roles",
                "update": "'admin' in @user.roles",
                "delete": "'admin' in @user.roles"
              }
            }
          }
        }
        """;

    /// <summary>
    /// The same project with one nullable field added — a rolling deploy caught mid-flight, where the replica
    /// that loses the race holds a <em>different</em> descriptor from the one that won.
    /// </summary>
    internal const string DriftedDescriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "cold-start",
          "description": "The next revision of the same project, deployed while the previous one is still starting.",
          "auth": { "providers": ["local"], "roles": ["admin"] },
          "entities": {
            "depots": {
              "description": "A depot.",
              "fields": {
                "code": { "type": "string", "required": true, "unique": true, "maxLength": 20 },
                "region": { "type": "string", "maxLength": 40 }
              },
              "rules": {
                "list": "'authenticated' in @user.roles",
                "get": "'authenticated' in @user.roles",
                "create": "'admin' in @user.roles",
                "update": "'admin' in @user.roles",
                "delete": "'admin' in @user.roles"
              }
            }
          }
        }
        """;

    /// <summary>Cold-starts one replica per descriptor, all against the same database, at the same instant.</summary>
    /// <param name="connectToTheOneDatabase">
    /// Selects the provider and the connection every replica shares — the engine leg's only contribution.
    /// </param>
    /// <param name="descriptorPerReplica">
    /// The descriptor JSON each replica boots. Its length is the number of replicas.
    /// </param>
    /// <param name="ct">A token to cancel the race.</param>
    internal static async Task<ColdStartRace> RaceAsync(
        Action<IAlvoBuilder> connectToTheOneDatabase,
        IReadOnlyList<string> descriptorPerReplica,
        CancellationToken ct)
    {
        var descriptorFiles = descriptorPerReplica.Select(WriteToATemporaryFile).ToList();
        using var startTogether = new Barrier(descriptorPerReplica.Count);
        var replicas = descriptorFiles
            .Select((file, index) => Replica.Build(connectToTheOneDatabase, file, index, startTogether))
            .ToList();

        try
        {
            var outcomes = await Task.WhenAll(replicas.Select(replica => Task.Run(() => replica.StartAsync(ct), ct)));

            return new ColdStartRace(outcomes, await ReadTheHistoryAsync(replicas[0], ct));
        }
        finally
        {
            foreach (var replica in replicas)
            {
                await replica.DisposeAsync();
            }

            descriptorFiles.ForEach(TryDelete);
        }
    }

    private static async Task<IReadOnlyList<int>> ReadTheHistoryAsync(Replica replica, CancellationToken ct) =>
        [.. (await replica.Services.GetRequiredService<IDescriptorVersionStore>().ListAsync(Project, ct))
            .Select(version => version.Revision)];

    private static string WriteToATemporaryFile(string descriptorJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-cold-start-{Guid.NewGuid():N}.alvo.json");
        File.WriteAllText(path, descriptorJson);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>One replica: its own container, its own boot, and the probe counts its boot produced.</summary>
    private sealed class Replica(IHost host, int index, BootProbe probe) : IAsyncDisposable
    {
        private bool _started;

        internal IServiceProvider Services => host.Services;

        internal static Replica Build(
            Action<IAlvoBuilder> connectToTheOneDatabase, string descriptorFile, int index, Barrier startTogether)
        {
            var probe = new BootProbe(startTogether);
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Services.AddAlvo(alvo =>
            {
                connectToTheOneDatabase(alvo);
                alvo.FromDescriptor(descriptorFile);
            });
            probe.Install(builder.Services);

            return new Replica(builder.Build(), index, probe);
        }

        internal async Task<ColdStartOutcome> StartAsync(CancellationToken ct)
        {
            var failure = await TryStartAsync(ct);
            var state = host.Services.GetRequiredService<AlvoBootState>();

            return new ColdStartOutcome(
                index,
                state.Phase,
                state.AppliedRevision,
                failure,
                probe.AppliedSchemaReads,
                probe.SchemaWrites,
                probe.AppliedSchemaWrites,
                probe.Rendezvoused,
                probe.Trace);
        }

        public async ValueTask DisposeAsync()
        {
            if (_started)
            {
                await host.StopAsync(CancellationToken.None);
            }

            host.Dispose();
        }

        private async Task<Exception?> TryStartAsync(CancellationToken ct)
        {
            try
            {
                await host.StartAsync(ct);
                _started = true;
                return null;
            }
            catch (Exception failure)
            {
                return failure;
            }
        }
    }

    /// <summary>
    /// Counts what one replica's boot did to the two ports the convergence turns on, and holds the barrier
    /// that makes the writes collide.
    /// </summary>
    /// <remarks>
    /// The production implementations stay in the path — each port is <em>decorated</em>, never replaced — so
    /// what is measured is the real driver racing the real driver, not a fake agreeing with itself.
    /// </remarks>
    private sealed class BootProbe(Barrier startTogether)
    {
        private readonly List<string> _trace = [];

        internal int AppliedSchemaReads { get; private set; }

        internal int SchemaWrites { get; private set; }

        internal int AppliedSchemaWrites { get; private set; }

        internal bool Rendezvoused { get; private set; }

        internal IReadOnlyList<string> Trace => _trace;

        internal void Install(IServiceCollection services)
        {
            Decorate<IAppliedSchemaStore>(services, inner => new CountingAppliedSchemaStore(inner, this));
            Decorate<IRuntimeSchemaWriter>(services, inner => new RacingRuntimeSchemaWriter(inner, this));
        }

        internal void ReadingTheAppliedSchema() => AppliedSchemaReads++;

        internal void ReadTheAppliedSchema(AppliedSchema? applied) =>
            _trace.Add($"read rev {applied?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

        internal void ReadingTheAppliedSchemaFailed(Exception failure) =>
            _trace.Add($"read threw {failure.GetType().Name}: {Summarise(failure)}");

        internal void EnteringTheSchemaWrite()
        {
            SchemaWrites++;
            if (SchemaWrites == 1)
            {
                Rendezvoused = startTogether.SignalAndWait(_rendezvousTimeout);
                _trace.Add(Rendezvoused ? "met the others" : "NOBODY MET — the race did not happen");
            }
        }

        internal void TheSchemaWriteApplied(int revision)
        {
            AppliedSchemaWrites++;
            _trace.Add($"wrote rev {revision}");
        }

        internal void TheSchemaWriteFailed(Exception failure) =>
            _trace.Add($"write threw {failure.GetType().Name}: {Summarise(failure)}");

        private static string Summarise(Exception failure) =>
            failure.Message.ReplaceLineEndings(" ") is { Length: > 120 } verbose
                ? verbose[..120]
                : failure.Message.ReplaceLineEndings(" ");

        private static void Decorate<TPort>(IServiceCollection services, Func<TPort, TPort> decorate)
            where TPort : class
        {
            var registered = services.Last(service => service.ServiceType == typeof(TPort));
            var inner = registered.ImplementationFactory
                ?? throw new InvalidOperationException(
                    $"{typeof(TPort).Name} is no longer registered through a factory, so this probe cannot "
                    + "decorate it without also taking over its construction.");

            services.Remove(registered);
            services.AddSingleton(provider => decorate((TPort)inner(provider)));
        }
    }

    private sealed class CountingAppliedSchemaStore(IAppliedSchemaStore inner, BootProbe probe) : IAppliedSchemaStore
    {
        public async Task<AppliedSchema?> GetCurrentAsync(string project, CancellationToken ct = default)
        {
            probe.ReadingTheAppliedSchema();
            try
            {
                var applied = await inner.GetCurrentAsync(project, ct);
                probe.ReadTheAppliedSchema(applied);
                return applied;
            }
            catch (Exception failure)
            {
                probe.ReadingTheAppliedSchemaFailed(failure);
                throw;
            }
        }

        public Task SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct = default) =>
            inner.SaveAsync(project, snapshot, ct);
    }

    /// <summary>
    /// The barrier itself: every replica is held at the door of the one atomic apply-and-append and released
    /// with the others, so the collision the fact is about actually happens.
    /// </summary>
    private sealed class RacingRuntimeSchemaWriter(IRuntimeSchemaWriter inner, BootProbe probe)
        : IRuntimeSchemaWriter
    {
        public async Task<DescriptorVersion> ApplyAndAppendAsync(
            string project, MigrationPlan plan, DescriptorVersion candidate,
            int expectedRevision, MigrationOptions options, CancellationToken ct = default)
        {
            probe.EnteringTheSchemaWrite();
            try
            {
                var written =
                    await inner.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct);
                probe.TheSchemaWriteApplied(written.Revision);
                return written;
            }
            catch (Exception failure)
            {
                probe.TheSchemaWriteFailed(failure);
                throw;
            }
        }
    }
}
