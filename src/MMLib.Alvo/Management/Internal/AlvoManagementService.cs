using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Reflection;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The one implementation of <see cref="IAlvoManagement"/>: it orchestrates services that already exist and
/// owns no connection, no SQL and no second copy of any rule.
/// </summary>
/// <remarks>
/// <b><paramref name="data"/> is genuinely optional, and it arrives through a factory registration rather
/// than as a nullable parameter for that reason</b> — a nullable constructor parameter is not an optional
/// dependency to the container (<c>AddAlvo</c>'s own remarks say so), so taking it the ordinary way would
/// turn <c>AddAlvo</c> without a driver into an activation failure on the first <c>info</c> request.
/// </remarks>
/// <param name="alvo">The deployment options the mode is read from.</param>
/// <param name="management">The management options a host's own mode label is read from.</param>
/// <param name="schema">The schema options the startup mode is read from.</param>
/// <param name="boot">What the boot published about which projects this instance serves.</param>
/// <param name="schemaRegistry">
/// The resolved schema the Data API's routes were generated from. It carries no project parameter — one
/// instance serves one project, which is the same constraint <c>GET projects</c> reports — so
/// <see cref="EnsureServed"/> is what keeps an unknown name a refusal rather than this project's answer.
/// </param>
/// <param name="policies">
/// <b>The engine the request path calls, resolved from DI rather than re-implemented.</b> It is what makes
/// the simulator's answer identical to production's by construction; a second evaluator would agree until
/// the day one of them was edited.
/// </param>
/// <param name="roles">The declared role catalog a simulated caller's role names are resolved through.</param>
/// <param name="data">The registered data port, or <see langword="null"/> when the host registered none.</param>
/// <param name="versions">
/// The descriptor history, or <see langword="null"/> when no provider registered one.
/// </param>
/// <param name="idempotency">
/// Where a write's outcome is filed under the caller's key, or <see langword="null"/> when no provider
/// registered one. Optional for <paramref name="versions"/>' reason, and read through <see cref="Keys"/>,
/// which refuses loudly rather than ignoring a key it cannot record.
/// </param>
/// <param name="callers">
/// Where the caller resolved for this request is published — read only to scope an idempotency key, never
/// to decide admission, which is <c>ManagementAccessEndpointFilter</c>'s alone.
/// </param>
/// <param name="logger">
/// Where a record that could not be filed for a write that already landed is reported — see
/// <see cref="FiledAsync"/> for why that is a warning rather than the caller's problem.
/// </param>
/// <param name="runtime">
/// <b>The apply path, resolved lazily.</b> <see cref="RuntimeSchemaService"/> needs
/// <see cref="IRuntimeSchemaWriter"/> and <see cref="IDescriptorVersionStore"/>, which only a database
/// provider registers — and unlike <paramref name="data"/> it cannot be resolved optionally, because a
/// container that registered the type and not its dependencies throws on activation rather than answering
/// <see langword="null"/>. A delegate defers that activation to the first apply, which is unreachable in a
/// driver-less container for <see cref="History"/>'s reason: no store, no boot, no project, so
/// <see cref="EnsureServed"/> has already answered 404.
/// </param>
internal sealed partial class AlvoManagementService(
    IOptions<AlvoOptions> alvo,
    IOptions<AlvoManagementOptions> management,
    IOptions<AlvoSchemaOptions> schema,
    AlvoBootState boot,
    ISchemaRegistry schemaRegistry,
    IPolicyEngine policies,
    IRoleCatalogProvider roles,
    IAlvoData? data,
    IDescriptorVersionStore? versions,
    IManagementIdempotencyStore? idempotency,
    IAlvoContextAccessor callers,
    ILogger<AlvoManagementService> logger,
    Func<RuntimeSchemaService> runtime) : IAlvoManagement
{
    /// <summary>What <see cref="ManagementInfo.DataProvider"/> reports when no driver is registered.</summary>
    private const string NoDriverRegistered = "none";

    /// <inheritdoc/>
    public Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ManagementInfo(Version, Mode, DataProvider, StartupMode));

    /// <inheritdoc/>
    public Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManagementProject>>(
            [.. boot.Projects.Select(entry =>
                new ManagementProject(entry.Key, boot.RevisionOf(entry.Key), Lower(entry.Value)))]);

    /// <inheritdoc/>
    public async Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        var current = await History.GetCurrentAsync(project, ct).ConfigureAwait(false);

        return new ManagementDescriptor(project, current?.Revision ?? 0, current?.DescriptorJson ?? string.Empty);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(
        string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        var history = await History.ListAsync(project, ct).ConfigureAwait(false);

        return [.. history.Select(Provenance)];
    }

    /// <inheritdoc/>
    public async Task<ManagementRevisionDetail> GetRevisionAsync(
        string project, int revision, CancellationToken ct = default)
    {
        EnsureServed(project);
        var stored = await History.GetAsync(project, revision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, revision);

        return new ManagementRevisionDetail(Provenance(stored), stored.DescriptorJson);
    }

    /// <inheritdoc/>
    public Task<SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);

        return Task.FromResult(schemaRegistry.GetSchema());
    }

    /// <inheritdoc/>
    public Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);

        return Task.FromResult(CapabilityReport.Project());
    }

    /// <inheritdoc/>
    public Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
    {
        EnsureServed(project);
        EnsureAnswerable(simulation);

        var decision = policies.Resolve(
            simulation.Entity, Operation(simulation.Operation), Caller(simulation.Caller));

        return Task.FromResult(Verdict(decision));
    }

    /// <inheritdoc/>
    public async Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default)
    {
        EnsureServed(project);
        ArgumentNullException.ThrowIfNull(request);

        var token = TokenFor(request.IdempotencyKey, request.DryRun, () => FingerprintOf(project, request));
        var replayed = await ReplayAsync(project, token, ct).ConfigureAwait(false);

        return replayed
            ?? await RecordingAsync(token, () => AppliedAsync(project, request, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Previews, guards, then either reports the plan or appends the revision.</summary>
    /// <param name="project">The project being changed.</param>
    /// <param name="request">What the caller asked for.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ManagementApplyResult> AppliedAsync(
        string project, ManagementApplyRequest request, CancellationToken ct)
    {
        var options = OptionsFor(request);
        var schema = runtime();
        var preview = await schema.PreviewAsync(
            project, request.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false);
        Guard(project, preview);

        return request.DryRun
            ? new ManagementApplyResult(Applied: false, preview.CurrentRevision, Summary(preview.Plan))
            : await AppendAsync(schema, project, request, options, preview, ct).ConfigureAwait(false);
    }

    /// <summary>What makes two applies of this project the same request.</summary>
    /// <param name="project">The project being changed.</param>
    /// <param name="request">What the caller asked for.</param>
    private static string FingerprintOf(string project, ManagementApplyRequest request) =>
        ManagementIdempotency.FingerprintOf(
            nameof(ApplyDescriptorAsync), project, request.ExpectedRevision, request.AllowDestructive,
            request.DescriptorJson);

    /// <inheritdoc/>
    public async Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)
    {
        EnsureServed(project);
        ArgumentNullException.ThrowIfNull(request);

        var token = TokenFor(
            request.IdempotencyKey, request.DryRun, () => FingerprintOf(project, targetRevision, request));
        var replayed = await ReplayAsync(project, token, ct).ConfigureAwait(false);

        return replayed ?? await RecordingAsync(
            token, () => RolledBackAsync(project, targetRevision, request, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Previews the reverse migration, guards it, then either reports it or appends it.</summary>
    /// <remarks>
    /// <b>The target is looked up here rather than left to the runtime path</b>, because that one answers a
    /// missing revision with an <see cref="InvalidOperationException"/> — a broken-invariant family, which
    /// over HTTP is a 500. A caller naming a revision that was never appended is asking an ordinary question
    /// with a 404 for an answer, which is <see cref="ManagementRevisionNotFoundException"/>'s whole job.
    /// </remarks>
    /// <param name="project">The project being restored.</param>
    /// <param name="targetRevision">The revision to restore.</param>
    /// <param name="request">What the caller asked for.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ManagementApplyResult> RolledBackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct)
    {
        var target = await History.GetAsync(project, targetRevision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, targetRevision);
        var options = OptionsFor(request.AllowDestructive, request.Author, request.Reason);
        var schema = runtime();
        var preview = await schema.PreviewAsync(
            project, target.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false);
        Guard(project, preview);

        return request.DryRun
            ? new ManagementApplyResult(Applied: false, preview.CurrentRevision, Summary(preview.Plan))
            : await RevertedAsync(schema, project, targetRevision, options, preview, ct).ConfigureAwait(false);
    }

    /// <summary>Appends the reverse migration, and reports the preview's plan beside the new revision.</summary>
    /// <remarks>
    /// It plans twice for <see cref="AppendAsync"/>'s reason, and the reverse plan is the one an operator
    /// most wants to see: it is the list of what a restore is about to drop.
    /// </remarks>
    /// <param name="schema">The runtime apply path.</param>
    /// <param name="project">The project being restored.</param>
    /// <param name="targetRevision">The revision being restored.</param>
    /// <param name="options">The migration options the request resolved to.</param>
    /// <param name="preview">What the plan-only pass reported.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<ManagementApplyResult> RevertedAsync(
        RuntimeSchemaService schema, string project, int targetRevision, MigrationOptions options,
        DescriptorApplyPreview preview, CancellationToken ct)
    {
        var reverted = await schema.RollbackAsync(project, targetRevision, options, ct).ConfigureAwait(false);

        return new ManagementApplyResult(Applied: true, reverted.Revision, Summary(preview.Plan));
    }

    /// <summary>What makes two rollbacks of this project the same request.</summary>
    /// <remarks>
    /// The target revision is the payload, because it is the whole of what this write carries beyond the
    /// base and the allowance — and the operation name is in the hash too, so one key spent on an apply can
    /// never be answered with a rollback's revision.
    /// </remarks>
    /// <param name="project">The project being restored.</param>
    /// <param name="targetRevision">The revision to restore.</param>
    /// <param name="request">What the caller asked for.</param>
    private static string FingerprintOf(
        string project, int targetRevision, ManagementRollbackRequest request) =>
        ManagementIdempotency.FingerprintOf(
            nameof(RollbackAsync), project, request.ExpectedRevision, request.AllowDestructive,
            targetRevision.ToString(CultureInfo.InvariantCulture));

    /// <summary>Applies what the preview described, and reports the preview's plan beside the new revision.</summary>
    /// <remarks>
    /// <b>The apply path plans twice, and that is deliberate.</b> <see cref="RuntimeSchemaService.ApplyAsync"/>
    /// does not return its plan and the response owes the caller a diff; planning is a pure function of two
    /// schemas and takes no lock, so the second call costs a diff and buys the editor's confirmation view.
    /// The alternative is widening <see cref="RuntimeSchemaService.ApplyAsync"/>'s public return type for a
    /// rendering convenience, which is a breaking change. If the second plan ever shows up in the load gate,
    /// widen it then; the cost is recorded here rather than left to be rediscovered.
    /// </remarks>
    /// <param name="schema">The runtime apply path.</param>
    /// <param name="project">The project being changed.</param>
    /// <param name="request">What the caller asked for.</param>
    /// <param name="options">The migration options the request resolved to.</param>
    /// <param name="preview">What the plan-only pass reported.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<ManagementApplyResult> AppendAsync(
        RuntimeSchemaService schema, string project, ManagementApplyRequest request,
        MigrationOptions options, DescriptorApplyPreview preview, CancellationToken ct)
    {
        var applied = await schema.ApplyAsync(
            project, request.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false);

        return new ManagementApplyResult(Applied: true, applied.Revision, Summary(preview.Plan));
    }

    /// <summary>
    /// The key this request carries, resolved into a record's identity — or <see langword="null"/> when it
    /// carries none.
    /// </summary>
    /// <remarks>
    /// <b>Every rule about the key itself is the port's</b>: a blank key, one past
    /// <c>AlvoIdempotency.MaxKeyBytes</c> UTF-8 bytes, and a holder with no identity to scope by are all
    /// refused by <c>AlvoIdempotency.EnsureUsableKey</c>, with the wordings the Data API already publishes.
    /// Restating any of the three here would be a second spelling of one rule — and the embedded host that
    /// calls this member directly has to meet the same three.
    /// </remarks>
    /// <param name="key">The key the request carries, if any.</param>
    /// <param name="dryRun">Whether this request writes nothing.</param>
    /// <param name="fingerprint">
    /// What makes this the same request on a retry, computed only once a key is actually present.
    /// </param>
    /// <exception cref="ManagementRequestException">The key is one this surface cannot honour.</exception>
    private ManagementIdempotencyToken? TokenFor(string? key, bool dryRun, Func<string> fingerprint)
    {
        if (key is null)
        {
            return null;
        }

        RefuseAKeyOnADryRun(dryRun);
        var caller = callers.Principal?.Context ?? AlvoContext.Anonymous;
        EnsureUsable(key, caller);

        return new ManagementIdempotencyToken(key, AlvoIdempotency.IdentityOf(caller), fingerprint());
    }

    /// <summary>Refuses a key on a request that appends nothing.</summary>
    /// <remarks>
    /// A dry run has no revision to replay and none to record. Accepting the key would file a record for a
    /// request that changed nothing, and turn the caller's later <em>real</em> apply into a replay of a
    /// preview — reporting a revision nobody appended.
    /// </remarks>
    /// <param name="dryRun">Whether this request writes nothing.</param>
    /// <exception cref="ManagementRequestException">It is a dry run.</exception>
    private static void RefuseAKeyOnADryRun(bool dryRun)
    {
        if (dryRun)
        {
            throw new ManagementRequestException(
                "A dry run may not carry an 'Idempotency-Key'. It appends no revision, so there is nothing "
                + "to replay and nothing to record — and a record filed here would turn your later real "
                + "write into a replay of a preview. Send the key with the write itself.");
        }
    }

    /// <summary>Applies the port's own key rules, restating the refusal as this surface's own type.</summary>
    /// <remarks>
    /// The message is carried verbatim, including the <c>(Parameter '…')</c> suffix
    /// <see cref="ArgumentException"/> itself appends: an in-process caller is reading a .NET exception and
    /// that suffix is information. The HTTP layer strips it before it becomes a <c>detail</c>, through
    /// <c>ProblemResultFactory.WithoutArgumentDetail</c>, because an internal argument name is not part of
    /// the contract an agent reads.
    /// </remarks>
    /// <param name="key">The key the caller sent.</param>
    /// <param name="caller">The caller the write is performed as.</param>
    /// <exception cref="ManagementRequestException">The key cannot be recorded for this caller.</exception>
    private static void EnsureUsable(string key, AlvoContext caller)
    {
        try
        {
            AlvoIdempotency.EnsureUsableKey(key, caller);
        }
        catch (ArgumentException refusal)
        {
            throw new ManagementRequestException(refusal.Message, refusal);
        }
    }

    /// <summary>
    /// What a previous identical request already appended, or <see langword="null"/> when this is not a
    /// replay.
    /// </summary>
    /// <remarks>
    /// <b>The recorded revision is re-read rather than trusted.</b> The record holds a number; the answer
    /// owes the caller a revision that still exists, and a record pointing at one that does not is a broken
    /// invariant rather than a replay.
    /// </remarks>
    /// <param name="project">The project the key was spent on.</param>
    /// <param name="token">The caller's resolved key, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AlvoIdempotencyConflictException">The key was spent on a different request.</exception>
    private async Task<ManagementApplyResult?> ReplayAsync(
        string project, ManagementIdempotencyToken? token, CancellationToken ct)
    {
        if (token is not { } spent)
        {
            return null;
        }

        var recorded = await Keys.FindAsync(spent.Key, spent.Scope, spent.Fingerprint, ct).ConfigureAwait(false);
        if (recorded is not { } revision)
        {
            return null;
        }

        var stored = await History.GetAsync(project, revision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, revision);

        return new ManagementApplyResult(Applied: true, stored.Revision, NothingRanNow);
    }

    /// <summary>Runs the write and files its revision under the caller's key.</summary>
    /// <remarks>
    /// <b>The record is written after the write commits, and the window is real.</b>
    /// <see cref="IRuntimeSchemaWriter.ApplyAndAppendAsync"/> owns its transaction and exposes no seam to
    /// enlist in, so a crash between that commit and this record leaves the attempt unrecorded — and the
    /// retry is refused with the unattributable 412 again. That narrows the window rather than closing it;
    /// closing it needs a widened writer, and the cost is stated rather than discovered.
    /// </remarks>
    /// <param name="token">The caller's resolved key, if any.</param>
    /// <param name="write">The write to perform.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ManagementApplyResult> RecordingAsync(
        ManagementIdempotencyToken? token, Func<Task<ManagementApplyResult>> write, CancellationToken ct)
    {
        var result = await write().ConfigureAwait(false);
        if (token is { } spent && result.Applied)
        {
            await FiledAsync(spent, result.Revision, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Files the record, and <b>refuses to fail the write because of it</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The migration and the revision are already committed when this runs</b>, so a failure here is a
    /// failure of bookkeeping for a write that is done. Letting it out would tell the caller their apply
    /// failed when it landed — the exact confusion the key exists to remove, inverted, and strictly worse
    /// than the 412 they would have had with no key at all. The consequence of swallowing it is the
    /// documented one and nothing more: the retry is unrecorded, so it is that same 412.
    /// </para>
    /// <para>
    /// <b>The catch is broad on purpose.</b> A store is a provider, and what a provider fails with is its
    /// own business — narrowing this to the exception types today's driver happens to raise would let the
    /// next one reintroduce the defect. Cancellation is excluded, because a cancelled request is the caller
    /// leaving rather than the store failing, and it is the one case that must still propagate.
    /// </para>
    /// </remarks>
    /// <param name="token">The caller's resolved key.</param>
    /// <param name="revision">The revision that was appended.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task FiledAsync(ManagementIdempotencyToken token, int revision, CancellationToken ct)
    {
        try
        {
            await Keys.RecordAsync(token.Key, token.Scope, token.Fingerprint, revision, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            RecordWasNotFiled(logger, revision, failure);
        }
    }

    /// <summary>The one record of a write that landed and whose key could not be filed.</summary>
    /// <remarks>
    /// <b>A warning and not an error</b>, because nothing is wrong with the data: the revision is applied and
    /// readable. What is lost is the caller's ability to attribute a retry, so the message says exactly that
    /// and names the revision an operator would otherwise have to go looking for.
    /// </remarks>
    /// <param name="logger">The logger to write through.</param>
    /// <param name="revision">The revision that was appended and not recorded.</param>
    /// <param name="failure">What the store answered with.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo applied revision {Revision} and could not record the caller's idempotency key for "
            + "it. The write landed and is not at risk. What is lost is only the retry's attribution: a "
            + "caller who repeats that request now gets 412 rather than a replay, which is the behaviour "
            + "they would have had without a key at all.")]
    private static partial void RecordWasNotFiled(ILogger logger, int revision, Exception failure);

    /// <summary>The plan a replay reports: none, because this request ran no migration.</summary>
    /// <remarks>
    /// The original apply's plan was not stored — the response owed a diff, not the history did — and it
    /// cannot be re-planned from a base that has moved. <c>GET revisions/{n}</c> is where what a revision
    /// applied is read from; this field says what <em>this</em> request did, which is nothing.
    /// </remarks>
    private static ManagementPlanSummary NothingRanNow => new(IsEmpty: true, HasDestructiveChanges: false, []);

    /// <summary>Refuses a plan that discards data the caller never asked to lose.</summary>
    /// <remarks>
    /// <b>Applied to the dry run too.</b> A preview that reported a plan the apply would then refuse would
    /// tell an editor its change is ready when it is not — and it is the framework's own refusal, so the
    /// endpoint maps one exception type for both branches.
    /// </remarks>
    /// <param name="project">The project the plan was built for.</param>
    /// <param name="preview">What the plan-only pass reported.</param>
    /// <exception cref="DestructiveChangeNotAllowedException">The guardrail refused the plan.</exception>
    private static void Guard(string project, DescriptorApplyPreview preview)
    {
        if (!preview.AllowedByGuardrail)
        {
            throw new DestructiveChangeNotAllowedException(project, preview.Plan);
        }
    }

    /// <summary>The migration options one apply resolves to.</summary>
    /// <param name="request">The request as it was bound.</param>
    private static MigrationOptions OptionsFor(ManagementApplyRequest request) =>
        OptionsFor(request.AllowDestructive, request.Author, request.Reason);

    /// <summary>The migration options one write's three allowances resolve to.</summary>
    /// <remarks>
    /// A blank <paramref name="reason"/> is passed through rather than defaulted here, so a rollback gets
    /// <c>RuntimeSchemaService.RollbackAsync</c>'s own <c>Rollback to revision N</c> — one wording for one
    /// fact, rather than a second copy of it in this layer.
    /// </remarks>
    /// <param name="allowDestructive">Whether a plan that discards data may proceed.</param>
    /// <param name="author">Who is writing.</param>
    /// <param name="reason">Why, or <see langword="null"/> to leave the framework's own default.</param>
    private static MigrationOptions OptionsFor(bool allowDestructive, string? author, string? reason) => new()
    {
        AllowDestructive = allowDestructive,
        Author = author,
        Reason = reason,
    };

    /// <summary>One plan, in the shape a diff view needs.</summary>
    /// <remarks>
    /// The step lines are <c>DestructiveChangeGuard</c>'s own, split back into a list: the wording an
    /// operator sees in a refused boot and the wording the dashboard renders are then the same sentence,
    /// rather than two formatters that agree until one is edited.
    /// </remarks>
    /// <param name="plan">The plan to project.</param>
    private static ManagementPlanSummary Summary(MigrationPlan plan) => new(
        plan.IsEmpty,
        plan.HasDestructiveChanges,
        [.. DestructiveChangeGuard.DescribeAllSteps(plan).Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Refuses a simulation the engine could only answer by guessing at what was meant.</summary>
    /// <remarks>
    /// An absent entity would reach <c>IPolicyEngine.Resolve</c> as a blank name and come back as a deny,
    /// which reads as a policy answer to a request that never asked a policy question.
    /// </remarks>
    /// <param name="simulation">The simulation as it was bound from the request.</param>
    /// <exception cref="ManagementSimulationException">It names no entity or no caller.</exception>
    private static void EnsureAnswerable(ManagementPolicySimulation? simulation)
    {
        if (simulation?.Caller is null || string.IsNullOrWhiteSpace(simulation.Entity))
        {
            throw new ManagementSimulationException(
                "A simulation needs an 'entity', an 'operation' and a 'caller'. Send all three: the engine "
                + "answers a triple, and a missing part would be answered as a denial rather than refused.");
        }
    }

    /// <summary>One operation's wire name, as the framework's own single mapping spells it.</summary>
    /// <remarks>
    /// Ordinal, like every other name in the framework — <c>List</c> is not <c>list</c>, it is a different
    /// name — and read through <c>ToWireName</c> rather than through <c>Enum.Parse</c>, so this and the
    /// descriptor's <c>rules.&lt;operation&gt;</c> keys cannot drift apart.
    /// </remarks>
    /// <param name="wireName">The operation name as the caller sent it.</param>
    /// <exception cref="ManagementSimulationException">It is not an operation this framework has.</exception>
    private static DataOperation Operation(string wireName) =>
        Enum.GetValues<DataOperation>()
            .Cast<DataOperation?>()
            .FirstOrDefault(operation =>
                string.Equals(operation!.Value.ToWireName(), wireName, StringComparison.Ordinal))
        ?? throw new ManagementSimulationException(
            $"'{wireName}' is not an operation. Use one of: "
            + $"{string.Join(", ", Enum.GetValues<DataOperation>().Select(operation => operation.ToWireName()))}.");

    /// <summary>The <see cref="AlvoContext"/> a simulated caller resolves to, exactly as a credential would.</summary>
    /// <param name="caller">The caller to simulate.</param>
    /// <exception cref="ManagementSimulationException">A role is undeclared, or the caller is not one production can produce.</exception>
    private AlvoContext Caller(ManagementSimulatedCaller caller)
    {
        if (caller.User is not { } user)
        {
            return Anonymous(caller.Roles);
        }

        try
        {
            return new AlvoContext
            {
                User = new UserId(user),
                Roles = (roles.DeclaredRoles ?? RoleCatalog.BuiltInOnly).Resolve(caller.Roles ?? []),
                Tenant = caller.Tenant is { } tenant ? new TenantId(tenant) : null,
            };
        }
        catch (UnknownRoleException refusal)
        {
            throw new ManagementSimulationException(refusal.Message, refusal);
        }
        catch (ArgumentException refusal)
        {
            throw new ManagementSimulationException(
                $"{refusal.Message} Send at least one role, or omit 'user' to simulate the anonymous caller.",
                refusal);
        }
    }

    /// <summary>The anonymous caller, refusing a request that gave them roles they could not hold.</summary>
    /// <remarks>
    /// Silently dropping the roles would answer the anonymous caller's question under the sender's own role
    /// names — a verdict that looks like an answer and is about somebody else.
    /// </remarks>
    /// <param name="named">The role names the request sent beside no identity.</param>
    private static AlvoContext Anonymous(IReadOnlyList<string>? named) =>
        named is null or { Count: 0 } || (named.Count == 1 && named[0] == Role.Anon.Name)
            ? AlvoContext.Anonymous
            : throw new ManagementSimulationException(
                "A simulation with no 'user' is the anonymous caller, who holds only 'anon'. Send a 'user' "
                + "for a caller that holds roles: no credential resolves to roles without an identity, so "
                + "this is not a caller production can produce.");

    /// <summary>The engine's decision, rendered.</summary>
    /// <remarks>
    /// Each predicate is published as its <b>CEL source</b>, which is the descriptor's own rule text — the
    /// same text this caller already reads from <c>GET projects/{p}/descriptor</c>, so nothing is disclosed
    /// here that the management gate has not already admitted them to.
    /// </remarks>
    /// <param name="decision">What the engine resolved.</param>
    private static ManagementPolicyVerdict Verdict(PolicyDecision decision) => new(
        !decision.IsDenied,
        decision.DenyReason,
        decision.Using?.Source,
        decision.WithCheck?.Source,
        decision.TenantScope?.Source,
        [.. decision.HiddenFields.Order(StringComparer.Ordinal)],
        [.. decision.ReadOnlyFields.Order(StringComparer.Ordinal)]);

    /// <summary>One stored revision's provenance, without the descriptor body a list has no use for.</summary>
    /// <param name="version">The stored revision.</param>
    private static ManagementRevision Provenance(DescriptorVersion version) => new(
        version.Revision, version.CreatedAt, version.Author, version.Reason, version.RolledBackFrom);

    /// <summary>Refuses a project name this instance did not boot, before anything reads a store for it.</summary>
    /// <remarks>
    /// <para>
    /// The boot is the one authority on which projects exist here, so this is the only check any member
    /// needs — and it is what keeps an unknown name a 404 rather than another project's answer, on a surface
    /// whose collaborators (<c>ISchemaRegistry</c>, <c>IPolicyEngine</c>) still carry no project parameter at
    /// all.
    /// </para>
    /// <para>
    /// <b>A blank name is refused by the lookup, not by an argument guard, and the difference is a status
    /// code.</b> <c>ThrowIfNullOrWhiteSpace</c> stood here and made <c>projects/%20/descriptor</c> an
    /// <see cref="System.ArgumentException"/> — family 5, which nothing in
    /// <c>ManagementEndpoints.Answer</c> catches, so a shipped host rendered it as a <b>500</b>. That is the
    /// exact outcome <see cref="ManagementProjectNotFoundException"/>'s own remarks say that type exists to
    /// prevent, and it reached every route carrying a <c>{project}</c> segment. A blank name is not a broken
    /// invariant; it is a caller naming a project that cannot exist, which is the same question a merely
    /// wrong name asks. The dictionary is ordinal and answers a blank key perfectly well, so the miss below
    /// is the whole check.
    /// </para>
    /// <para>
    /// <see langword="null"/> is still family 5, and deliberately so: it cannot arrive over HTTP — a matched
    /// route segment is never null — so it means an in-process caller passed one, which is the caller's own
    /// broken invariant rather than a question about a project.
    /// </para>
    /// </remarks>
    /// <param name="project">The project name the caller asked for.</param>
    /// <exception cref="ManagementProjectNotFoundException">This instance serves no such project.</exception>
    private void EnsureServed(string project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!boot.Projects.ContainsKey(project))
        {
            throw new ManagementProjectNotFoundException(project, [.. boot.Projects.Keys]);
        }
    }

    /// <summary>
    /// The descriptor history, or a refusal naming what is missing.
    /// </summary>
    /// <remarks>
    /// <b>Unreachable behind <see cref="EnsureServed"/>, and stated rather than assumed.</b> Only a database
    /// provider registers an <see cref="IDescriptorVersionStore"/>, and only a boot that read one publishes a
    /// project — so a container with no store serves no project and every member here has already answered
    /// 404. It is resolved optionally anyway, because <c>AddAlvo</c> with no driver is a supported
    /// composition and <see cref="IAlvoManagement"/> has to activate in it.
    /// </remarks>
    private IDescriptorVersionStore History =>
        versions ?? throw new InvalidOperationException(
            "No IDescriptorVersionStore is registered, so this instance has no descriptor history to read. "
            + "Register a database provider inside AddAlvo(...).");

    /// <summary>
    /// Where a write's outcome is filed under the caller's key, or a refusal naming what is missing.
    /// </summary>
    /// <remarks>
    /// <b>A host with a key and no store fails loudly rather than serving the write without it.</b> Ignoring
    /// a key is exactly the lost retry the key exists to prevent, so a deployment that cannot honour one has
    /// to say so — the same reading <c>GET capabilities</c> applies to every "declared but not honoured"
    /// affordance. It is unreachable behind <see cref="EnsureServed"/> for <see cref="History"/>'s reason:
    /// only a database provider registers one, and only a boot that read one publishes a project.
    /// </remarks>
    private IManagementIdempotencyStore Keys =>
        idempotency ?? throw new InvalidOperationException(
            "No IManagementIdempotencyStore is registered, so an idempotency key cannot be recorded — and "
            + "ignoring one would be the lost retry the key exists to prevent. Register a database provider "
            + "inside AddAlvo(...), or send this write without a key.");

    /// <summary>One enum value as the wire spells it.</summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="value">The value to name.</param>
    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>The running build, as the assembly itself records it.</summary>
    private static string Version =>
        typeof(AlvoManagementService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AlvoManagementService).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>The host's own label when it set one, otherwise the mode it registered.</summary>
    private string Mode => management.Value.ModeLabel ?? alvo.Value.Mode.ToString().ToLowerInvariant();

    /// <summary>The registered port's type name, or that there is none.</summary>
    private string DataProvider => data?.GetType().Name ?? NoDriverRegistered;

    /// <summary>The startup mode this process booted under.</summary>
    private string StartupMode => schema.Value.Startup.ToString().ToLowerInvariant();
}
