using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
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
/// </remarks>
internal sealed partial class AlvoBootService : IHostedLifecycleService
{
    private readonly DescriptorBootPlan _bootPlan;
    private readonly ISchemaMigrator _migrator;
    private readonly ISchemaIntrospector _introspector;
    private readonly IAppliedSchemaStore _store;
    private readonly IPolicyCatalogProvider _policyCatalogProvider;
    private readonly AlvoBootState _state;
    private readonly IOptions<AlvoSchemaOptions> _options;
    private readonly ILogger<AlvoBootService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AlvoBootService"/> class.</summary>
    /// <param name="bootPlan">Stage 0: the descriptor, the schema it wants, and its compiled policy.</param>
    /// <param name="migrator">Plans and applies the difference between the two schemas.</param>
    /// <param name="introspector">Reports the live schema when Alvo has recorded none for this project.</param>
    /// <param name="store">The applied snapshot, whose storage is also stage 1's system schema.</param>
    /// <param name="policyCatalogProvider">Where stage 3 publishes the compiled catalog.</param>
    /// <param name="state">What the boot publishes for a readiness probe to read.</param>
    /// <param name="options">The startup mode and destructive allowance, already validated by the host.</param>
    /// <param name="logger">Records what the boot decided, so a restart's no-op is visible.</param>
    public AlvoBootService(
        DescriptorBootPlan bootPlan,
        ISchemaMigrator migrator,
        ISchemaIntrospector introspector,
        IAppliedSchemaStore store,
        IPolicyCatalogProvider policyCatalogProvider,
        AlvoBootState state,
        IOptions<AlvoSchemaOptions> options,
        ILogger<AlvoBootService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootPlan);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policyCatalogProvider);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _bootPlan = bootPlan;
        _migrator = migrator;
        _introspector = introspector;
        _store = store;
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
    /// A refusal is recorded on <see cref="AlvoBootState"/> by the stage that raises it, so the catch in
    /// <see cref="StartingAsync"/> does not overwrite a project-scoped failure with a project-less one.
    /// </remarks>
    private async Task BootAsync(CancellationToken ct)
    {
        var boot = await _bootPlan.LoadAsync(ct).ConfigureAwait(false);
        var project = boot.Descriptor.Name;

        var applied = await ReadAppliedSchemaAsync(project, ct).ConfigureAwait(false);
        var decision = await DecideAsync(boot, applied, ct).ConfigureAwait(false);
        var revision = await CarryOutAsync(boot, applied, decision, ct).ConfigureAwait(false);

        _state.Ready(project, revision);
        BootIsReady(_logger, project, decision.Outcome.ToString(), revision);
    }

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

        return SchemaStartupPolicy.Decide(applied, plan, _options.Value);
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

        return decision.Outcome is SchemaStartupOutcome.Unchanged
            ? Prime(boot, applied?.Revision)
            : await ApplyAsync(boot, applied, decision, ct).ConfigureAwait(false);
    }

    /// <summary>Records the refusal for a probe to read, then stops the start.</summary>
    /// <remarks>
    /// The state is written <em>before</em> the throw, so the phase is <see cref="AlvoBootPhase.Failed"/> for
    /// anything that can still read it. Nothing can, on the strong path — a refused boot never reaches
    /// <c>StartedAsync</c>, so the server never binds — and that redundancy is the point: readiness must be
    /// structurally incapable of reporting Ready for a schema that was refused, not merely unreachable.
    /// </remarks>
    [DoesNotReturn]
    private void RefuseTheBoot(string project, SchemaStartupDecision decision)
    {
        var refusal = decision.Refusal ?? UnexplainedRefusal;

        _state.Failed(project, refusal);

        throw new AlvoStartupRefusedException(refusal, decision.Fix ?? string.Empty);
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
    /// Initializes or migrates the project schema, records the new snapshot, and primes from it.
    /// </summary>
    /// <remarks>
    /// The order is the one <see cref="SchemaMigrationRunner"/> established and must not be relaxed: the
    /// snapshot is saved only after the DDL succeeded, and the catalog is published only after the snapshot is
    /// saved, so nothing ever serves rules for a schema the database does not have.
    /// </remarks>
    private async Task<int?> ApplyAsync(
        BootPlan boot, AppliedSchema? applied, SchemaStartupDecision decision, CancellationToken ct)
    {
        var result = await _migrator.ApplyAsync(decision.Plan, MigrationOptions, ct).ConfigureAwait(false);
        result.EnsureApplied();

        var revision = (applied?.Revision ?? 0) + 1;
        var snapshot = new AppliedSchema(boot.Desired, boot.DescriptorJson, revision, DateTimeOffset.UtcNow);
        await _store.SaveAsync(boot.Descriptor.Name, snapshot, ct).ConfigureAwait(false);

        return Prime(boot, revision);
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

    /// <summary>The one record of what a boot did, as a compile-time-generated <c>LoggerMessage</c> delegate.</summary>
    /// <remarks>
    /// Source-generated because <c>CA1848</c> is an error in this repository. Logged at information level, and
    /// naming the outcome, because "the restart applied nothing and primed" and "the boot initialized the
    /// database" are the two events an operator reading a container's first ten lines is trying to tell apart.
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
        ILogger logger, string project, string outcome, int? appliedRevision);
}
