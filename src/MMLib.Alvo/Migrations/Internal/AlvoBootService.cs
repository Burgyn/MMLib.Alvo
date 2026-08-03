using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Migrations.Internal;

/// <summary>
/// The boot sequence, as the host lifecycle's own first act: load and validate the descriptor, bring the
/// schema Alvo needs up as far as the mode allows, prime the policy catalog from it, and publish
/// <see cref="AlvoBootState"/> — all of it <b>before the server binds</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IHostedLifecycleService.StartingAsync"/>, not <c>StartAsync</c>, and the difference is a
/// guarantee rather than a preference.</b> <c>StartingAsync</c> runs before <em>every</em>
/// <see cref="IHostedService.StartAsync"/>, including <c>GenericWebHostService</c>'s, which is the call that
/// binds the socket — measured, and recorded as fact 7 of the design
/// (<c>docs/superpowers/specs/evidence/2026-08-02-startup-lifecycle/spike.txt</c>). A plain
/// <see cref="IHostedService"/> would also work today, but only because
/// <c>WebApplicationBuilder.Build()</c> appends the web host's service after every user-registered one
/// (aspnetcore#36122) — a behaviour with no API-level guarantee, and one that is sensitive to when
/// <c>AddAlvo</c> was called. So the fact that pins this observes that nothing could be served yet, not that
/// the code says <c>StartingAsync</c>.
/// </para>
/// <para>
/// <b>Options validation is not a stage here, because the framework has already done it.</b>
/// <c>Host.StartAsync</c> runs every <c>ValidateOnStart</c> registration <em>before</em> any
/// <c>StartingAsync</c> (design fact 8), so a mistyped credential refuses the start before this service runs
/// at all — which is exactly the ordering
/// <c>A_credential_the_startup_validation_refuses_leaves_the_database_untouched</c> pins. Calling
/// <see cref="IStartupValidator"/> by hand here would re-run stateless validators for no gain and imply the
/// guarantee is this service's rather than the host lifecycle's.
/// </para>
/// <para>
/// <b>Stage 1 — the framework's own <c>alvo.*</c> tables — has no port of its own, and that is deliberate
/// rather than an omission.</b> A:508/A:515 require the system schema to come up automatically at startup on a
/// chain independent of the host's tables, and it does: it is owned by whichever driver implements
/// <see cref="IAppliedSchemaStore"/>, which cannot answer a single call without it, and the EF Core driver
/// creates it idempotently (once, race-guarded) on first use. So <see cref="ReadAppliedSchemaAsync"/> is
/// stages 1 and 2's read at once, in every mode. The core could not do better today even if it wanted to: the
/// initializer is <c>internal</c> to the driver package, and the core depends on
/// <c>MMLib.Alvo.Abstractions</c> alone (<c>package-boundary.md</c>). A port is <em>earned</em> the moment a
/// driver's system schema grows a table no store call touches — PR5's outbox is the first candidate — and not
/// before.
/// </para>
/// <para>
/// <b>What runs where.</b> Stage 0 is <see cref="DescriptorBootPlan"/> and touches no database. Stage 2's
/// decision is <see cref="SchemaStartupPolicy"/> and is a pure function. This service therefore decides
/// nothing; it sequences, carries out, and publishes. Routes are not stage 4's business either — they
/// materialise from the primed registry at enumeration time, after this has finished.
/// </para>
/// <para>
/// <b>Several replicas cold-starting against one empty database converge instead of crash-looping</b> — see
/// <see cref="ConvergeOnWhatTheDatabaseSaysAsync"/> for the bounded single retry that makes that true, and
/// <see cref="ApplyAsync"/> for why the write goes through <see cref="IRuntimeSchemaWriter"/> rather than
/// <see cref="ISchemaMigrator.ApplyAsync"/> followed by <see cref="IAppliedSchemaStore.SaveAsync"/>.
/// </para>
/// </remarks>
internal sealed partial class AlvoBootService : IHostedLifecycleService
{
    private readonly DescriptorBootPlan _bootPlan;
    private readonly ISchemaMigrator _migrator;
    private readonly IRuntimeSchemaWriter _writer;
    private readonly ISchemaIntrospector _introspector;
    private readonly IAppliedSchemaStore _store;
    private readonly IDescriptorVersionStore _history;
    private readonly IPolicyCatalogProvider _policyCatalogProvider;
    private readonly AlvoBootState _state;
    private readonly IOptions<AlvoSchemaOptions> _options;
    private readonly ILogger<AlvoBootService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AlvoBootService"/> class.</summary>
    /// <param name="bootPlan">Stage 0: the descriptor, the schema it wants, and its compiled policy.</param>
    /// <param name="migrator">Plans the difference between the two schemas.</param>
    /// <param name="writer">Applies that plan and records it as one transaction.</param>
    /// <param name="introspector">Reports the live schema when Alvo has recorded none for this project.</param>
    /// <param name="store">The applied snapshot, whose storage is also stage 1's system schema.</param>
    /// <param name="history">
    /// The project's append-only descriptor history, read to decide whether this process is holding a
    /// descriptor the database has already moved on from — see <see cref="AmIAnOlderPodAsync"/>.
    /// </param>
    /// <param name="policyCatalogProvider">Where stage 3 publishes the compiled catalog.</param>
    /// <param name="state">What the boot publishes for a readiness probe to read.</param>
    /// <param name="options">The startup mode and destructive allowance, already validated by the host.</param>
    /// <param name="logger">Records what the boot decided, so a restart's no-op is visible.</param>
    public AlvoBootService(
        DescriptorBootPlan bootPlan,
        ISchemaMigrator migrator,
        IRuntimeSchemaWriter writer,
        ISchemaIntrospector introspector,
        IAppliedSchemaStore store,
        IDescriptorVersionStore history,
        IPolicyCatalogProvider policyCatalogProvider,
        AlvoBootState state,
        IOptions<AlvoSchemaOptions> options,
        ILogger<AlvoBootService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootPlan);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policyCatalogProvider);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _bootPlan = bootPlan;
        _migrator = migrator;
        _writer = writer;
        _introspector = introspector;
        _store = store;
        _history = history;
        _policyCatalogProvider = policyCatalogProvider;
        _state = state;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs the whole boot, before the server binds.</summary>
    /// <param name="cancellationToken">Cancels the boot, failing the start.</param>
    /// <exception cref="AlvoStartupRefusedException">
    /// The descriptor cannot be served against this database under the configured mode.
    /// </exception>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BootAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AlvoStartupRefusedException)
        {
            throw;
        }
        catch (Exception failure)
        {
            _state.Failed(failure.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The five stages, in the one order that keeps each at its own risk level.</summary>
    /// <remarks>
    /// <para>
    /// A refusal is recorded on <see cref="AlvoBootState"/> by the stage that raises it, so the catch in
    /// <see cref="StartingAsync"/> does not overwrite a project-scoped failure with a project-less one.
    /// </para>
    /// <para>
    /// <b>A stand-down returns here without publishing <see cref="AlvoBootState.Ready"/>.</b> It is the one
    /// outcome that neither serves nor stops the process, so the boot has to end without either — see
    /// <see cref="StandDown"/>. Publishing Ready first and overwriting it after would leave a window in which a
    /// readiness probe reads Ready for a project that will never serve.
    /// </para>
    /// </remarks>
    private async Task BootAsync(CancellationToken ct)
    {
        var boot = await LoadTheDescriptorAsync(ct).ConfigureAwait(false);
        var project = boot.Descriptor.Name;

        var (outcome, revision) = await ConvergeOnWhatTheDatabaseSaysAsync(boot, ct).ConfigureAwait(false);
        if (outcome is SchemaStartupOutcome.StandDown)
        {
            return;
        }

        _state.Ready(project, revision);
        RecordWhatTheBootDid(project, outcome, revision);
    }

    /// <summary>Stage 0, with its refusal recorded before it propagates.</summary>
    /// <remarks>
    /// <b>A stage-0 refusal has no project name, and it must still leave the phase
    /// <see cref="AlvoBootPhase.Failed"/> rather than <see cref="AlvoBootPhase.Pending"/></b> — which is what
    /// <see cref="AlvoBootState.Failed(string)"/> exists for and what its remarks require. Recorded here, at the
    /// stage that raises it, for the same reason stage 2's refusal is recorded in
    /// <see cref="RefuseTheBoot"/>: the catch in <see cref="StartingAsync"/> rethrows an
    /// <see cref="AlvoStartupRefusedException"/> untouched so that it cannot overwrite a project-scoped refusal
    /// with a project-less one, and a stage-0 refusal that nothing recorded therefore left an embedded host
    /// unable to tell "no descriptor configured" from "the boot has not run".
    /// </remarks>
    private async Task<BootPlan> LoadTheDescriptorAsync(CancellationToken ct)
    {
        try
        {
            return await _bootPlan.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (AlvoStartupRefusedException refusal)
        {
            _state.Failed(refusal.Message);
            BootRefused(_logger, refusal.Message);

            throw;
        }
    }

    /// <summary>
    /// Stages 1–3, and — if another replica got there first — once more, against what that replica left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three replicas cold-starting against one empty database all see "uninitialized", and only one of them
    /// can be right.</b> They are serialized by the optimistic append inside <see cref="IRuntimeSchemaWriter"/>:
    /// one commits revision 1, the losers are refused. A loser that threw would turn the ordinary first
    /// deployment of a replica set into a crash loop, so it re-reads and decides again — which is a required
    /// behaviour of this design, not a nicety.
    /// </para>
    /// <para>
    /// <b>Re-deciding is not adopting.</b> The second pass runs the same
    /// <see cref="SchemaStartupPolicy.Decide"/> over what the winner actually committed. With the same
    /// descriptor the plan comes back empty and the loser primes and serves. With a <em>different</em>
    /// descriptor — a rolling deploy caught mid-flight — the loser is now looking at ordinary drift and is
    /// governed by the ordinary mode: under <see cref="AlvoSchemaStartupMode.Verify"/> it refuses, and under
    /// the default <see cref="AlvoSchemaStartupMode.Apply"/> it applies its <em>own</em> descriptor over the
    /// winner's <em>if that plan adds only</em>. Either way it never silently serves the winner's schema, which
    /// would leave the process running rules compiled against a schema it never agreed to.
    /// </para>
    /// <para>
    /// <b>They do not, however, take turns rewriting the schema — measured, and the opposite of what this
    /// remark used to say.</b> A loser whose descriptor lacks a field the winner applied plans a
    /// <em>drop</em> of it, and the destructive gate refuses a drop in every mode. So the schema does not
    /// oscillate: the replica holding the subset descriptor cannot start at all, which is the concrete shape
    /// of the reason a production deployment sets <see cref="AlvoSchemaStartupMode.Verify"/> and applies from
    /// a migration job instead.
    /// </para>
    /// <para>
    /// <b>What the race can no longer decide is <em>which generation</em> of the descriptor wins</b> —
    /// <see cref="AmIAnOlderPodAsync"/> answers that from the append-only history before the plan is judged, so
    /// a replica holding a descriptor the database has already moved on from stands down instead of applying
    /// its own over a newer one (#145). That closes the cases the destructive gate cannot see, because they
    /// discard nothing: an index or constraint added one way and dropped the other, and a pair of declared
    /// renames pointing at each other.
    /// </para>
    /// <para>
    /// <b>At most one retry, and no lock.</b> A loop would hang a boot instead of failing it, which is strictly
    /// worse for an operator and for an orchestrator that is already willing to restart the container. And a
    /// lock is the shape to avoid outright: EF Core's SQLite migration lock is a table row with no timeout that
    /// survives a killed process, so an OOM-kill mid-migration wedges every later boot until someone deletes the
    /// row by hand. The revision check has nothing to leak.
    /// </para>
    /// </remarks>
    private async Task<BootOutcome> ConvergeOnWhatTheDatabaseSaysAsync(BootPlan boot, CancellationToken ct)
    {
        try
        {
            return await DecideAndCarryOutAsync(boot, ct).ConfigureAwait(false);
        }
        catch (Exception lostRace) when (IsAnotherWriterGettingThereFirst(lostRace))
        {
            var conflict = lostRace.GetType().Name;
            AnotherReplicaWonTheRace(_logger, boot.Descriptor.Name, conflict);

            return await DecideAndCarryOutAsync(boot, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="failure"/> is the shape of another writer having got to the schema first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="DescriptorConcurrencyException"/> is what both shipped engines were measured to raise</b>,
    /// because the write goes through <see cref="IRuntimeSchemaWriter"/> and the version-row insert is
    /// therefore the gate: three replicas racing one empty SQLite file and one empty PostgreSQL database both
    /// produced a clean optimistic-lock loss for every loser. With the write ordered the other way round — DDL
    /// first — SQLite instead produced <c>table "…" already exists</c>, which says nothing about who won; see
    /// <see cref="ApplyAsync"/>.
    /// </para>
    /// <para>
    /// <b><see cref="DbException"/> is a belt, and an honestly unexercised one.</b>
    /// <c>VersionRowWriter.ThrowIfConcurrencyConflictAsync</c> documents the path that reaches it: when the
    /// insert fails on lock contention and the re-read still shows the old revision, the raw engine failure is
    /// rethrown rather than dressed up as a conflict. On SQLite that never happened across the runs measured
    /// here — the loser's own re-read has to take the write lock, so it serializes behind the winner's commit
    /// and sees the new revision — but it is reachable in principle, and a different journal mode, busy
    /// timeout or engine could reach it. It is kept because the failure it would otherwise cause is precisely
    /// the crash loop this method exists to prevent, and the cost of being wrong is one extra bounded attempt.
    /// </para>
    /// <para>
    /// Being broad is safe because of what follows rather than because of the classification: the retry
    /// re-reads and re-decides from scratch, so a failure that was <em>not</em> a lost race reaches the same
    /// conclusion again and this time propagates. A narrower filter would have to name provider error codes,
    /// which is exactly the engine-specific knowledge the core must not hold.
    /// </para>
    /// </remarks>
    /// <param name="failure">What the first pass threw.</param>
    private static bool IsAnotherWriterGettingThereFirst(Exception failure) =>
        failure is DescriptorConcurrencyException or DbException;

    /// <summary>Reads what the database says, decides what that allows, and does it.</summary>
    /// <remarks>
    /// One pass, so the retry above is literally the same pass over a database that has meanwhile moved —
    /// rather than a second, subtly different code path that only the losing replica ever executes.
    /// </remarks>
    private async Task<BootOutcome> DecideAndCarryOutAsync(BootPlan boot, CancellationToken ct)
    {
        var applied = await ReadAppliedSchemaAsync(boot.Descriptor.Name, ct).ConfigureAwait(false);
        var decision = await DecideAsync(boot, applied, ct).ConfigureAwait(false);
        var revision = await CarryOutAsync(boot, applied, decision, ct).ConfigureAwait(false);

        return new BootOutcome(decision.Outcome, revision);
    }

    /// <summary>What one pass of the boot concluded.</summary>
    /// <param name="Outcome">What stage 2 decided.</param>
    /// <param name="AppliedRevision">The applied revision the process is serving, if any.</param>
    private readonly record struct BootOutcome(SchemaStartupOutcome Outcome, int? AppliedRevision);

    /// <summary>
    /// Stage 1 and stage 2's read: the applied snapshot, over storage the driver brings up on first use.
    /// </summary>
    /// <remarks>
    /// Unconditional, in every mode including <see cref="AlvoSchemaStartupMode.Skip"/>, because stage 1 is
    /// unconditional — see the type's remarks for why the system schema has no port of its own and why this one
    /// call is both stages. <see cref="AlvoSchemaStartupMode.Skip"/> therefore <em>reads</em> the store and
    /// then ignores what it found, which is a deliberate narrowing of the design's "do not read the store": the
    /// read is idempotent, and the tables it touches are the ones stage 1 has to create anyway.
    /// </remarks>
    /// <param name="project">The project whose snapshot to read.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<AppliedSchema?> ReadAppliedSchemaAsync(string project, CancellationToken ct) =>
        _store.GetCurrentAsync(project, ct);

    /// <summary>Stage 2: plan the difference, then let the pure policy decide what may be done about it.</summary>
    /// <remarks>
    /// The current side of the plan falls back to a live introspection when Alvo has recorded no snapshot,
    /// exactly as <see cref="SchemaMigrationRunner"/> does — which is precisely why an "initialize" plan can
    /// contain drops, and why the destructive guardrail sits ahead of the initialize branch inside
    /// <see cref="SchemaStartupPolicy"/>.
    /// </remarks>
    private async Task<SchemaStartupDecision> DecideAsync(
        BootPlan boot, AppliedSchema? applied, CancellationToken ct)
    {
        var current = applied?.Schema ?? await _introspector.IntrospectAsync(ct).ConfigureAwait(false);
        var plan = await _migrator.PlanAsync(current, boot.Desired, MigrationOptions, ct).ConfigureAwait(false);
        var outOfOrder = await AmIAnOlderPodAsync(boot, plan, ct).ConfigureAwait(false);

        return SchemaStartupPolicy.Decide(applied, plan, _options.Value, outOfOrder);
    }

    /// <summary>
    /// Whether this process is holding a descriptor the database has already moved on from — the ordering the
    /// <see cref="AlvoSchemaStartupMode.Apply"/> default otherwise leaves to a race (#145).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not asked when there is nothing to apply, and that is what keeps an ordinary restart cheap.</b>
    /// <see cref="IDescriptorVersionStore.ListAsync"/> reads the whole history, which is O(N) in a project's
    /// applied revisions, so paying it on the most common boot in existence — a restart over an unchanged
    /// descriptor — to be told something the empty plan already implies would be a real tax for nothing. The
    /// gate governs the <em>apply</em>: a boot that changes no schema cannot be the one that rewrites a newer
    /// schema with an older one.
    /// </para>
    /// <para>
    /// <b>Read here rather than reusing the applied snapshot.</b> <see cref="ReadAppliedSchemaAsync"/> returns
    /// only the current row, and the question is whether <em>this</em> descriptor is somewhere behind it — which
    /// only the history can answer. Deriving the current snapshot from <c>history[^1]</c> instead, and dropping
    /// the other read, was considered and declined: that read is also stage 1, and every probe count measured
    /// against it in the concurrency facts is pinned to the port it goes through.
    /// </para>
    /// </remarks>
    /// <param name="boot">Stage 0's descriptor and the JSON it was loaded from.</param>
    /// <param name="plan">The plan stage 2 is about to judge.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<OutOfOrderBoot?> AmIAnOlderPodAsync(
        BootPlan boot, MigrationPlan plan, CancellationToken ct)
    {
        if (plan.IsEmpty)
        {
            return null;
        }

        var history = await _history.ListAsync(boot.Descriptor.Name, ct).ConfigureAwait(false);

        return DescriptorHistoryOrder.Check(boot.Descriptor, boot.DescriptorJson, history);
    }

    /// <summary>Stages 2 and 3: do what was decided, and prime from the descriptor that was accepted.</summary>
    /// <returns>The applied revision the process is serving, or <see langword="null"/> when it read none.</returns>
    private async Task<int?> CarryOutAsync(
        BootPlan boot, AppliedSchema? applied, SchemaStartupDecision decision, CancellationToken ct)
    {
        if (decision.Outcome is SchemaStartupOutcome.Refuse)
        {
            RefuseTheBoot(boot.Descriptor.Name, decision);
        }

        if (decision.Outcome is SchemaStartupOutcome.StandDown)
        {
            return StandDown(boot.Descriptor.Name, decision);
        }

        return decision.Outcome is SchemaStartupOutcome.Unchanged
            ? Prime(boot, applied?.Revision)
            : await ApplyAsync(boot, applied, decision, ct).ConfigureAwait(false);
    }

    /// <summary>Records the refusal for a probe to read, then stops the start.</summary>
    /// <remarks>
    /// <para>
    /// The state is written <em>before</em> the throw, so the phase is <see cref="AlvoBootPhase.Failed"/> for
    /// anything that can still read it. Nothing can, on the strong path — a refused boot never reaches
    /// <c>StartedAsync</c>, so the server never binds — and that redundancy is the point: readiness must be
    /// structurally incapable of reporting Ready for a schema that was refused, not merely unreachable.
    /// </para>
    /// <para>
    /// It is also <em>logged</em>, and that is not decoration: <see cref="AlvoBootState.Failure"/> is deliberately
    /// withheld from the readiness body (design deviation 59), so the log is where the design and
    /// <c>MapAlvoHealth</c>'s own remarks promise an operator finds the reason. Without this call the promise was
    /// only true for a standalone host reading stderr.
    /// </para>
    /// </remarks>
    [DoesNotReturn]
    private void RefuseTheBoot(string project, SchemaStartupDecision decision)
    {
        var refusal = decision.Refusal ?? UnexplainedRefusal;

        _state.Failed(project, refusal);
        BootRefused(_logger, refusal);

        throw new AlvoStartupRefusedException(refusal, decision.Fix ?? string.Empty);
    }

    /// <summary>
    /// Records that this process is behind the database, primes nothing, and lets the start finish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one outcome that neither serves nor stops the process, and the difference from
    /// <see cref="RefuseTheBoot"/> is the whole point.</b> A destructive or
    /// <see cref="AlvoSchemaStartupMode.Verify"/> refusal is a configuration or authoring error that only a
    /// human resolves, so it throws and the host exits 78 — the loudest, fastest feedback there is. An
    /// out-of-order boot is a position in a deployment: this pod is not misconfigured, it is behind, and
    /// exiting turns that into a crash loop an orchestrator will retry forever over a condition no restart can
    /// fix. So the phase goes to <see cref="AlvoBootPhase.Failed"/>, readiness answers 503, liveness keeps
    /// answering 200, and the pod is drained rather than killed — the shape the startup design's deviation 65
    /// established for a schema the Data API cannot route.
    /// </para>
    /// <para>
    /// <b>Nothing is primed, and that is the safety property rather than an omission.</b> An unprimed
    /// <see cref="IPolicyCatalogProvider"/> denies every operation and an unprimed
    /// <c>ISchemaRegistry</c> materialises an empty route table, so a process that stood down can answer 403
    /// and 404 and nothing else. Standing down therefore does not depend on nobody routing to it.
    /// </para>
    /// <para>
    /// Logged at <see cref="LogLevel.Critical"/> through the same call a refusal uses: this is where the design
    /// promises an operator finds the reason, because <see cref="AlvoBootState.Failure"/> is deliberately
    /// withheld from the readiness body (deviation 59) and — unlike a refusal — nothing here writes to stderr
    /// on the way out.
    /// </para>
    /// </remarks>
    /// <param name="project">The project whose boot stood down.</param>
    /// <param name="decision">Stage 2's verdict, carrying the operator-readable reason.</param>
    /// <returns>
    /// No applied revision, ever: this process primed from nothing, so there is nothing for it to report
    /// serving.
    /// </returns>
    private int? StandDown(string project, SchemaStartupDecision decision)
    {
        var refusal = decision.Refusal ?? UnexplainedRefusal;

        _state.Failed(project, refusal);
        BootRefused(_logger, refusal);

        return null;
    }

    /// <summary>
    /// Stage 3 on a boot that changes nothing: publish the catalog stage 0 already compiled.
    /// </summary>
    /// <remarks>
    /// <b>This is the gap <see cref="RuntimeSchemaService"/>'s own remarks call real.</b> Priming used to be
    /// something a successful <em>apply</em> did on the way past, so a restart over an unchanged descriptor —
    /// the ordinary case — came back with an unprimed provider, which denies every operation. Publishing the
    /// stage-0 catalog rather than calling <c>PolicyCatalogPriming.Prime</c> avoids compiling a second,
    /// identical catalog; the content is the same either way.
    /// </remarks>
    private int? Prime(BootPlan boot, int? revision)
    {
        _policyCatalogProvider.SetCurrent(boot.Descriptor.Name, boot.Catalog);

        return revision;
    }

    /// <summary>
    /// Initializes or migrates the project schema and records it as one transaction, then primes from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through <see cref="IRuntimeSchemaWriter"/>, not <see cref="ISchemaMigrator.ApplyAsync"/> followed by
    /// <see cref="IAppliedSchemaStore.SaveAsync"/> — and the difference is what makes a concurrent cold start
    /// converge at all.</b> That port's own remarks state the reason: it inserts the version row first, as the
    /// optimistic-lock gate, so the only writer that reaches the DDL is the confirmed winner. Applying first and
    /// recording second lets every replica run the DDL, which on SQLite was <em>measured</em> to fail the losers
    /// with <c>table "…" already exists</c> — a failure that says nothing about who won — and leaves a window in
    /// which the database holds the schema while no revision row explains it.
    /// </para>
    /// <para>
    /// The catalog is published only after that transaction commits, so nothing ever serves rules for a schema
    /// the database does not have.
    /// </para>
    /// </remarks>
    private async Task<int?> ApplyAsync(
        BootPlan boot, AppliedSchema? applied, SchemaStartupDecision decision, CancellationToken ct)
    {
        var project = boot.Descriptor.Name;
        RefuseToDiscardDataWhateverWasDecided(project, decision.Plan);

        var candidate = new DescriptorVersion(
            boot.Desired, boot.DescriptorJson, Revision: 0, DateTimeOffset.UtcNow);
        var written = await _writer
            .ApplyAndAppendAsync(project, decision.Plan, candidate, applied?.Revision ?? 0, MigrationOptions, ct)
            .ConfigureAwait(false);

        return Prime(boot, written.Revision);
    }

    /// <summary>The last guard before the DDL runs: a plan that discards data needs an explicit allowance.</summary>
    /// <remarks>
    /// <see cref="SchemaStartupPolicy.Decide"/> has already refused such a plan, so this is unreachable through
    /// it — which is the point. <see cref="IRuntimeSchemaWriter"/> executes whatever it is handed and
    /// deliberately re-evaluates no policy, so the guardrail that used to be
    /// <see cref="ISchemaMigrator.ApplyAsync"/>'s has to be restated here rather than quietly dropped when the
    /// write moved. It is the same check <see cref="RuntimeSchemaService"/> makes on the runtime path.
    /// </remarks>
    /// <param name="project">The project whose schema is being changed.</param>
    /// <param name="plan">The plan stage 2 cleared for execution.</param>
    private void RefuseToDiscardDataWhateverWasDecided(string project, MigrationPlan plan)
    {
        if (plan.HasDestructiveChanges && !_options.Value.AllowDestructive)
        {
            throw new DestructiveChangeNotAllowedException(project, plan);
        }
    }

    /// <summary>
    /// What the boot asks the migrator for. Never a dry run — a boot that planned without applying would
    /// report Ready over a schema it never brought up.
    /// </summary>
    private MigrationOptions MigrationOptions =>
        new() { AllowDestructive = _options.Value.AllowDestructive };

    /// <summary>
    /// The refusal used when the policy refused without saying why — unreachable through
    /// <see cref="SchemaStartupPolicy"/>, and a refusal with no text would be worse than a wrong one.
    /// </summary>
    private const string UnexplainedRefusal =
        "Alvo cannot start: the project schema was refused and no reason was recorded. This is a defect in "
        + "Alvo itself — please report it with the descriptor that produced it.";

    /// <summary>
    /// Records the boot's outcome at the level that outcome deserves — <see cref="LogLevel.Warning"/> for the one
    /// that used DDL rights on a database somebody was already running.
    /// </summary>
    /// <remarks>
    /// <b>An <em>applied drift</em> is not the same event as an initialize or a no-op restart, and levelling them
    /// together hid the one an operator has to see.</b> Initializing an empty database and priming an unchanged
    /// one are routine; rewriting the schema of a database that already held one is the cost of the
    /// <see cref="AlvoSchemaStartupMode.Apply"/> default being paid, and a production deployment that meant to set
    /// <see cref="AlvoSchemaStartupMode.Verify"/> and did not has exactly one chance to notice — this line.
    /// </remarks>
    /// <param name="project">The project that booted.</param>
    /// <param name="outcome">What stage 2 decided.</param>
    /// <param name="revision">The applied revision the process is serving, if any.</param>
    private void RecordWhatTheBootDid(string project, SchemaStartupOutcome outcome, int? revision)
    {
        if (outcome is SchemaStartupOutcome.Apply)
        {
            BootAppliedTheDrift(_logger, project, revision);

            return;
        }

        BootIsReady(_logger, project, outcome, revision);
    }

    /// <summary>The one record of what a boot did, as a compile-time-generated <c>LoggerMessage</c> delegate.</summary>
    /// <remarks>
    /// <para>
    /// Source-generated because <c>CA1848</c> is an error in this repository. Logged at information level, and
    /// naming the outcome, because "the restart applied nothing and primed" and "the boot initialized the
    /// database" are the two events an operator reading a container's first ten lines is trying to tell apart.
    /// </para>
    /// <para>
    /// The outcome is passed as the <see cref="SchemaStartupOutcome"/> itself rather than as
    /// <c>outcome.ToString()</c>: <c>CA1873</c> refuses an argument that is evaluated whether or not the level
    /// is enabled, and the generated code formats the value only when it logs. That rule ships in a newer SDK
    /// than <c>global.json</c> pins, so it is the <b>image build</b> — Alpine's <c>sdk:10.0-alpine</c>, and
    /// therefore <c>scripts/test-e2e</c> — that fails on it, with every local ring green.
    /// </para>
    /// </remarks>
    /// <param name="logger">The logger the boot writes through.</param>
    /// <param name="project">The project that booted.</param>
    /// <param name="outcome">What stage 2 decided.</param>
    /// <param name="appliedRevision">The applied revision the process is serving, if any.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo booted project {Project}: schema {Outcome}, serving applied revision "
            + "{AppliedRevision}.")]
    private static partial void BootIsReady(
        ILogger logger, string project, SchemaStartupOutcome outcome, int? appliedRevision);

    /// <summary>The record of a boot that brought a database somebody was already running up to the descriptor.</summary>
    /// <param name="logger">The logger the boot writes through.</param>
    /// <param name="project">The project that booted.</param>
    /// <param name="appliedRevision">The revision the apply wrote.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo applied the descriptor's drift to project {Project}'s schema on startup and is serving "
            + "applied revision {AppliedRevision}. This process holds DDL rights against its own database; a "
            + "production deployment sets Alvo__Schema__Startup=Verify and applies from a migration job.")]
    private static partial void BootAppliedTheDrift(ILogger logger, string project, int? appliedRevision);

    /// <summary>The record of a refused boot, which is where an operator is promised the reason.</summary>
    /// <remarks>
    /// Critical rather than error: the process is about to stop, and this line is the only place the refusal is
    /// readable for an <em>embedded</em> host — a standalone one also gets it on stderr. The reason may carry a
    /// provider's own message, which is why it goes to the log (governed by the host's redaction) and never to
    /// the anonymous readiness body.
    /// </remarks>
    /// <param name="logger">The logger the boot writes through.</param>
    /// <param name="refusal">The operator-readable refusal, fix lines included.</param>
    [LoggerMessage(Level = LogLevel.Critical, Message = "{Refusal}")]
    private static partial void BootRefused(ILogger logger, string refusal);

    /// <summary>The one record that this replica lost the cold-start race and is deciding again.</summary>
    /// <remarks>
    /// <para>
    /// Information rather than warning: on a replica set this is the <em>expected</em> outcome for every
    /// replica but one, and logging it as a warning would train an operator to ignore warnings. It is logged at
    /// all because it is the difference between "this boot initialized the database" and "this boot found it
    /// initialized while trying to", which no other line reports.
    /// </para>
    /// <para>
    /// <b>The conflict's <em>type</em>, never its message.</b> The lost race is diagnosed from a
    /// <see cref="DbException"/> that any third-party driver may raise, and that message is the class of text
    /// deviation 59 keeps off the probe; Information is the level most aggressively shipped to an aggregator, so
    /// it is the last place to put one. The type answers the only question this line asks — was it the optimistic
    /// gate or the engine — and a failure that was <em>not</em> a lost race propagates from the retry with its
    /// message intact.
    /// </para>
    /// </remarks>
    /// <param name="logger">The logger the boot writes through.</param>
    /// <param name="project">The project whose schema was being brought up.</param>
    /// <param name="conflict">The exception type the losing write reported.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo lost the schema race for project {Project} to another replica and is re-reading what "
            + "it applied. Conflict: {Conflict}")]
    private static partial void AnotherReplicaWonTheRace(ILogger logger, string project, string conflict);
}
