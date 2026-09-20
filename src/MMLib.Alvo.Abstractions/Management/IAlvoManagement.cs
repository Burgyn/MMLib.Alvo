namespace MMLib.Alvo.Management;

/// <summary>
/// <b>The one operation surface for administering an Alvo project.</b> The admin dashboard resolves it from
/// DI and calls it in-process; an agent, the CLI and a later MCP adapter reach the same members over HTTP.
/// One path, two transports — spec §0.5 contract 4 forbids a divergent write <em>path</em>, not
/// serialisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is descriptor-shaped and idempotent</b>, so an MCP adapter is a mapping rather than a
/// translation: there is no HTTP-only affordance an adapter would have to fake. A write carries its own
/// expected revision rather than reading it off a request header, which is what keeps the in-process
/// caller's semantics identical to the HTTP caller's.
/// </para>
/// <para>
/// <b>A write is at-most-once through that revision, and attributable through an optional key.</b> A
/// retried write names the revision it was written against, so the second attempt loses the optimistic-lock
/// race and is refused — nothing has to be stored for that to hold. What the revision cannot do is tell the
/// retrying caller <em>why</em> they were refused: "my own write landed and the response was lost" and
/// "somebody else changed the descriptor" are the same 412 and need opposite recoveries. An
/// <c>IdempotencyKey</c> on the request converts the first of them into a replay carrying the revision the
/// first attempt appended. It is optional, and honoured rather than declared — a request that carries one a
/// deployment cannot record is refused, never quietly served without it.
/// </para>
/// <para>
/// <b>Data is deliberately absent.</b> Rows are read and written through the Data API under the caller's own
/// context, so no management privilege over data exists and none has to be audited.
/// </para>
/// <para>
/// <b>Every member has an HTTP route, and a contract test holds that</b> — the drift this shape risks is an
/// operation reachable in-process and not over the wire. The test reads the <em>live endpoint table</em>, so
/// adding a member here without adding a route fails a build.
/// </para>
/// <para>
/// <b>Authorization is not this interface's job.</b> Over HTTP every route carries the gate
/// <c>access</c> compiles (<c>RequireAlvoManagementAccess</c>); an in-process caller holding this reference
/// has already been admitted by whatever composed it. Publishing two answers here and there is exactly the
/// divergence contract 4 exists to prevent.
/// </para>
/// </remarks>
public interface IAlvoManagement
{
    /// <summary>Describes this deployment: the build, the mode, the data provider and the startup mode.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What this instance is.</returns>
    Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Lists the projects this instance serves.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One entry per booted project; in this build, exactly one.</returns>
    Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default);

    /// <summary>
    /// The project's current descriptor, exactly as it was applied, with the revision an apply must echo in
    /// <c>If-Match</c>. <b>This is the export</b> — no re-serialisation happens.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored descriptor text and its revision.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default);

    /// <summary>The project's append-only configuration history, oldest revision first.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Every appended revision's provenance, without its descriptor body.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(string project, CancellationToken ct = default);

    /// <summary>One historical revision — the export of a past state.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revision's provenance and the descriptor it applied.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRevisionNotFoundException">That revision was never appended.</exception>
    Task<ManagementRevisionDetail> GetRevisionAsync(string project, int revision, CancellationToken ct = default);

    /// <summary>The resolved schema — what the Data API actually serves for this project.</summary>
    /// <remarks>
    /// The descriptor is what the author wrote; this is what survived. Where the two differ is where
    /// "declared but not honoured" lives, which is what the capability report enumerates.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The applied <see cref="Schema.SchemaModel"/>.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<Schema.SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default);

    /// <summary>What this build honours, warns about and refuses for this project.</summary>
    /// <remarks>
    /// <b>The prose is served verbatim.</b> Every consequence and every fix is the framework's own sentence,
    /// already covered by tests and already asserted against the frozen schema; a client that reworded one
    /// would be a third spelling of one truth.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The capability report.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default);

    /// <summary>
    /// Answers what a named caller may do to an entity — <b>by calling the same <c>IPolicyEngine</c>
    /// production calls</b>, never a copy of it.
    /// </summary>
    /// <remarks>
    /// That is the whole of the implementation: bind a context, call <c>Resolve</c>, render the decision.
    /// It is also the only reading under which "answers identically to production" is a property rather
    /// than a promise — a second evaluator would agree until the day one of them was edited.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="simulation">The entity, the operation and the caller to simulate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verdict and the predicates the engine resolved.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementSimulationException">
    /// The simulation names an entity, an operation, a role or a caller the framework cannot resolve.
    /// </exception>
    Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default);

    /// <summary>
    /// Applies a descriptor — <b>the one write path to a project's configuration.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="request"/> carries its own expected revision rather than reading it off a request
    /// header, which is what makes the in-process caller's semantics identical to the HTTP caller's. Over
    /// HTTP that integer arrives as <c>If-Match</c>.
    /// </para>
    /// <para>
    /// <b><see cref="ManagementApplyRequest.DryRun"/> plans and reports without writing</b>, and is refused
    /// by the same destructive guardrail a real apply is: a preview that reported a plan the apply would
    /// then refuse would tell an editor its change is ready when it is not.
    /// </para>
    /// <para>
    /// <b>A replay reports the revision it replays and an empty plan</b>, because this request performed no
    /// migration — the plan the original apply ran is <see cref="GetRevisionAsync"/>'s business, and
    /// re-planning it is impossible from a base that has moved.
    /// </para>
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="request">The descriptor, the expected revision, and the allowances.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was applied, what would be, or what a previous identical request already applied.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRequestException">
    /// The request carries an <see cref="ManagementApplyRequest.IdempotencyKey"/> this surface cannot honour
    /// — on a dry run, for a caller with no identity, or past the port's byte bound.
    /// </exception>
    /// <exception cref="Data.AlvoIdempotencyConflictException">
    /// The key was already spent by this caller on a different request.
    /// </exception>
    /// <exception cref="Descriptor.DescriptorValidationException">The descriptor is invalid.</exception>
    /// <exception cref="Migrations.DescriptorConcurrencyException">
    /// <see cref="ManagementApplyRequest.ExpectedRevision"/> is not the current one.
    /// </exception>
    /// <exception cref="Migrations.DestructiveChangeNotAllowedException">
    /// The plan discards data and <see cref="ManagementApplyRequest.AllowDestructive"/> is not set.
    /// </exception>
    Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Restores a past revision by appending the reverse migration as a new revision. <b>History is never
    /// rewritten.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A rollback is an apply of a past descriptor</b>, so it carries the same two allowances an apply
    /// does and reports the same result. <paramref name="targetRevision"/> and
    /// <see cref="ManagementRollbackRequest.ExpectedRevision"/> are two numbers with two jobs: the target
    /// says what to restore, the expected revision says from where.
    /// </para>
    /// <para>
    /// <b>The destructive guardrail is the point here, not a formality.</b> A reverse migration drops what
    /// the forward one added, so the refusal a caller most often meets on this member is the one that saves
    /// data they did not say they could lose.
    /// </para>
    /// <para>
    /// <b>Restoring a different <c>access</c> block is an authorization change.</b> Over HTTP the route's
    /// gate is <c>Developer</c> and the transport re-resolves the requirement to <c>Admin</c> when the
    /// target's block differs from the applied one — the same rule an apply is held to, and for the same
    /// reason: <c>access</c> lives inside the descriptor, so a history that ever held a looser block would
    /// otherwise be a standing escalation.
    /// </para>
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="targetRevision">The revision to restore.</param>
    /// <param name="request">The expected current revision, the allowances, and the provenance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended revision, or — for a dry run — the reverse plan and the base it was planned against.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRevisionNotFoundException">That revision was never appended.</exception>
    /// <exception cref="ManagementRequestException">
    /// The request carries an <see cref="ManagementRollbackRequest.IdempotencyKey"/> this surface cannot
    /// honour.
    /// </exception>
    /// <exception cref="Data.AlvoIdempotencyConflictException">
    /// The key was already spent by this caller on a different request.
    /// </exception>
    /// <exception cref="Migrations.DescriptorConcurrencyException">The expected revision is not the current one.</exception>
    /// <exception cref="Migrations.DestructiveChangeNotAllowedException">
    /// The reverse plan discards data and it was not allowed.
    /// </exception>
    Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default);
}
