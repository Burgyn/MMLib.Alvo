using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The dashboard's one reader over <see cref="IAlvoManagement"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a gateway rather than components injecting the contract directly.</b> Three of the
/// dashboard's screens need the descriptor, the schema and <c>capabilities</c> at once, and a
/// component that fetched each in its own <c>OnInitializedAsync</c> would make three round trips
/// per render pass and four when a child re-rendered. This holds one copy per circuit, for as long
/// as the operator stays on one screen.
/// </para>
/// <para>
/// <b>It caches, and the cache is therefore a correctness question rather than a performance
/// one.</b> <see cref="Invalidate"/> runs after every real apply this circuit makes, and on every
/// navigation. The second is what covers the applies this circuit did not make — another tab of
/// the same operator, another administrator, the CLI, an assistant turn — none of which this
/// circuit hears about: a scope is the circuit, and the working copy's <c>Changed</c> says an edit
/// moved, not that a revision did (docs/architecture/admin-dashboard-review.md, F-15). The reads
/// are in-process, so re-reading once per screen costs little, and a screen that stays open
/// across somebody else's apply is still safe: that apply moved the revision, so the next
/// <c>If-Match</c> this circuit sends is refused rather than silently overwriting them.
/// </para>
/// <para>
/// <b>It authorizes nothing.</b> Every call runs with the operator's principal published on
/// <see cref="IAlvoContextAccessor"/>, and the core decides — see <see cref="AsOperatorAsync{T}(Func{Task{T}}, CancellationToken)"/>.
/// </para>
/// </remarks>
/// <param name="management">The one management contract, resolved in-process (design §1.2).</param>
/// <param name="people">Administers membership, when a deployment has a membership store.</param>
/// <param name="callers">Turns the signed-in operator into the caller Alvo authorizes.</param>
/// <param name="authentication">Who is signed in on this circuit.</param>
/// <param name="ambient">Where a call publishes its caller for the core to read.</param>
internal sealed class ManagementGateway(
    IAlvoManagement management,
    IAlvoUserAdministration? people,
    IAlvoAdminCallerResolver callers,
    AuthenticationStateProvider authentication,
    IAlvoContextAccessor ambient) : IDisposable
{
    private NavigationManager? _navigation;
    private ManagementDescriptor? _descriptor;
    private SchemaModel? _schema;
    private ManagementCapabilities? _capabilities;
    private ManagementInfo? _info;
    private IReadOnlyList<ManagementProject>? _projects;

    /// <summary>The project this dashboard is looking at.</summary>
    /// <remarks>
    /// One project, and §2.6 says why: <c>GET {m}/projects</c> answers with exactly one today, so
    /// a switcher offering a choice would be offering a choice that does not exist. The property
    /// is here rather than inlined so that the day it becomes a choice, one place changes.
    /// </remarks>
    public string Project { get; private set; } = string.Empty;

    /// <summary>
    /// Raised after a real apply or rollback, so a component showing the applied revision can read it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists for the shell, which is mounted once and outlives the screen that applied.</b> The
    /// project card re-read only on navigation, and Preview stays where it is after an apply, so the card
    /// said "revision 1" beside a panel announcing revision 2.
    /// </para>
    /// <para>
    /// The same shape as <see cref="AssistantGateway.ConnectionChanged"/> and for the same reason: this
    /// scoped gateway is what the layout and the page share for a circuit, and the write path is the one
    /// place that knows the answer moved. A dry run raises nothing, because it wrote nothing.
    /// </para>
    /// </remarks>
    public event Action? Applied;

    /// <summary>The descriptor as stored, with the revision it is at.</summary>
    public async ValueTask<ManagementDescriptor> DescriptorAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _descriptor ??= await AsOperatorAsync(
            () => management.GetDescriptorAsync(project, ct), ct).ConfigureAwait(false);
    }

    /// <summary>The resolved schema — what the Data API actually serves.</summary>
    public async ValueTask<SchemaModel> SchemaAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _schema ??= await AsOperatorAsync(
            () => management.GetSchemaAsync(project, ct), ct).ConfigureAwait(false);
    }

    /// <summary>What this build honours, warns about and refuses.</summary>
    public async ValueTask<ManagementCapabilities> CapabilitiesAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _capabilities ??= await AsOperatorAsync(
            () => management.GetCapabilitiesAsync(project, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Build, mode, data provider and startup mode.</summary>
    public async ValueTask<ManagementInfo> InfoAsync(CancellationToken ct)
        => _info ??= await AsOperatorAsync(() => management.GetInfoAsync(ct), ct).ConfigureAwait(false);

    /// <summary>Every project this build manages — one, today (§2.6).</summary>
    public async ValueTask<IReadOnlyList<ManagementProject>> ProjectsAsync(CancellationToken ct)
        => _projects ??= await AsOperatorAsync(
            () => management.ListProjectsAsync(ct), ct).ConfigureAwait(false);

    /// <summary>The append-only configuration history, newest first.</summary>
    /// <remarks>
    /// Never cached: it is the one read whose whole purpose is to be current. Sorted here, because the
    /// contract answers oldest first and every screen reads newest first — see <see cref="RevisionHistory"/>.
    /// </remarks>
    public async Task<IReadOnlyList<ManagementRevision>> RevisionsAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        var revisions = await AsOperatorAsync(
            () => management.ListRevisionsAsync(project, ct), ct).ConfigureAwait(false);

        return RevisionHistory.NewestFirst(revisions);
    }

    /// <summary>One past revision, with the descriptor it applied.</summary>
    public async Task<ManagementRevisionDetail> RevisionAsync(int revision, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await AsOperatorAsync(
            () => management.GetRevisionAsync(project, revision, ct), ct).ConfigureAwait(false);
    }

    /// <summary>The engine's verdict for one caller, one entity and one operation.</summary>
    public async Task<ManagementPolicyVerdict> SimulateAsync(
        ManagementPolicySimulation simulation, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await AsOperatorAsync(
            () => management.SimulatePolicyAsync(project, simulation, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Applies a descriptor, or plans one when <paramref name="dryRun"/> is set.</summary>
    /// <remarks>
    /// A real apply invalidates every cached read, because every one of them can have moved. A dry
    /// run invalidates nothing, because by its own contract it wrote nothing.
    /// </remarks>
    public async Task<ManagementApplyResult> ApplyAsync(
        string descriptorJson, int expectedRevision, bool allowDestructive, bool dryRun,
        string? author, string? reason, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        var request = new ManagementApplyRequest(
            descriptorJson, expectedRevision, allowDestructive, dryRun, author, reason);

        var result = await AsOperatorAsync(
            () => management.ApplyDescriptorAsync(project, request, ct), ct).ConfigureAwait(false);

        if (!dryRun)
        {
            Invalidate();
            Applied?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// Restores a past revision by appending its reverse migration as a new revision.
    /// </summary>
    /// <remarks>
    /// Two revision numbers, and confusing them is the bug this signature exists to make hard:
    /// <paramref name="targetRevision"/> is the past state to restore, while
    /// <paramref name="expectedRevision"/> is the revision the caller believes is <em>current</em>
    /// — the same optimistic-concurrency token an apply sends, and the reason a rollback issued
    /// against a history somebody else has moved is refused rather than silently reordered.
    /// </remarks>
    public async Task<ManagementApplyResult> RollbackAsync(
        int targetRevision, int expectedRevision, bool allowDestructive, bool dryRun,
        string? author, string? reason, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        var request = new ManagementRollbackRequest(
            expectedRevision, allowDestructive, dryRun, author, reason);

        var result = await AsOperatorAsync(
            () => management.RollbackAsync(project, targetRevision, request, ct), ct).ConfigureAwait(false);

        if (!dryRun)
        {
            Invalidate();
            Applied?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// Who to record as the author of a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every apply this dashboard makes carries one.</b> A revision's <c>Author</c> is the only
    /// place a configuration change is attributed, Configuration history renders it forever, and
    /// §3.7 leans on exactly that when it argues that the recorded route is the one an
    /// administrator should prefer. An apply sent with no author reads as <i>code-first or
    /// system</i> — which is true of a mounted descriptor and false of a person clicking Apply.
    /// </para>
    /// <para>
    /// The address, because that is what another operator recognises; the subject id when the
    /// host's authentication did not supply one, because an opaque id is still better than nothing.
    /// </para>
    /// </remarks>
    public async ValueTask<string?> AuthorAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        var name = state.User.Identity?.Name;

        return name is { Length: > 0 }
            ? name
            : state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>Whether this deployment can administer people at all.</summary>
    /// <remarks>
    /// <see langword="false"/> when no package registered a membership store. The Access screen
    /// then says so rather than offering controls that would throw — the same rule as a refused
    /// feature: a control whose only possible outcome is a refusal is worse than its absence.
    /// </remarks>
    public bool CanAdministerPeople => people is not null;

    /// <summary>The signed-in operator's own user id, or <see langword="null"/> when they resolve to no caller.</summary>
    /// <remarks>
    /// For a screen that has to recognise the operator's own row — Access, where the core refuses a person
    /// granting themselves a tenant, and a control whose only outcome is that refusal should not be offered.
    /// It decides nothing: the core still refuses the call if a screen offers it anyway.
    /// </remarks>
    /// <param name="ct">Cancels the read of the membership store.</param>
    public async ValueTask<UserId?> SelfAsync(CancellationToken ct)
        => (await CallerAsync(ct).ConfigureAwait(false))?.Context.User;

    /// <summary>One page of the people on this project.</summary>
    public Task<AlvoUserPage> PeopleAsync(AlvoUserQuery query, CancellationToken ct)
        => AsOperatorAsync(() => Administration.ListAsync(query, ct), ct);

    /// <summary>Creates a person who can sign in, once somebody sets their password.</summary>
    public Task<AlvoUser> CreatePersonAsync(AlvoUserCreation creation, CancellationToken ct)
        => AsOperatorAsync(() => Administration.CreateAsync(creation, ct), ct);

    /// <summary>Replaces a person's roles.</summary>
    public Task<AlvoUser> SetRolesAsync(UserId user, IReadOnlyList<string> roleNames, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetRolesAsync(user, roleNames, ct), ct);

    /// <summary>Grants, changes or removes the one tenant a person acts in.</summary>
    public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetTenantAsync(user, tenant, ct), ct);

    /// <summary>Bars a person from signing in, or lets them back.</summary>
    public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetDisabledAsync(user, disabled, ct), ct);

    /// <summary>Mints the single-use token with which somebody sets their own password.</summary>
    public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken ct)
        => AsOperatorAsync(() => Administration.IssueCredentialTokenAsync(user, ct), ct);

    private IAlvoUserAdministration Administration => people
        ?? throw new InvalidOperationException(
            "This deployment registered no membership store, so there is nobody to administer. "
            + "CanAdministerPeople says so before a screen offers a control.");

    /// <summary>Writes the instance's AI connection, through the core's own admission ladder.</summary>
    /// <param name="connection">The endpoint, the model and the credential, as one record.</param>
    /// <param name="ct">A token to cancel the write.</param>
    public Task SetAiConnectionAsync(MMLib.Alvo.Ai.StoredAiConnection connection, CancellationToken ct)
        => AsOperatorAsync(async () =>
        {
            await management.SetAiConnectionAsync(connection, ct).ConfigureAwait(false);

            return true;
        }, ct);

    /// <summary>
    /// Runs a whole stream with the operator published, for as long as it is being consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An iterator rather than the <c>Func</c> overload, and the difference is load-bearing.</b>
    /// <c>IAlvoContextAccessor</c> is an <see cref="System.Threading.AsyncLocal{T}"/> holder, so a
    /// publication made inside a helper that then returns is gone by the time the caller enumerates
    /// anything. Publishing here, in the body that drives the inner enumeration, is what keeps the caller
    /// published for every <c>MoveNext</c> — which is what an assistant turn needs, because it calls the
    /// management surface several times between one update and the next.
    /// </para>
    /// <para>
    /// Without it every one of the agent's tool calls sees no principal, resolves to
    /// <c>AlvoContext.Anonymous</c>, and is refused — the safe direction, and a blind assistant.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the stream yields.</typeparam>
    /// <param name="stream">The stream to run.</param>
    /// <param name="ct">Cancels resolving the caller and the enumeration.</param>
    public async IAsyncEnumerable<T> AsOperatorAsync<T>(
        Func<IAsyncEnumerable<T>> stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var previous = ambient.Principal;
        ambient.Principal = await CallerAsync(ct).ConfigureAwait(false);

        try
        {
            await foreach (var item in stream().WithCancellation(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            ambient.Principal = previous;
        }
    }

    /// <summary>
    /// Drops the cached reads on every navigation of this circuit, so a screen reads the configuration as it
    /// is now rather than as it was when the circuit started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invalidate on navigation rather than key the cache by a revision</b>, which is the other shape the
    /// review offers. The only revision the dashboard could key on is one it saw applied, and an apply over the
    /// CLI or the HTTP route is seen by no part of it; a navigation is, and it is the moment an operator asks
    /// for a screen. No bus, no shared state: this circuit's own <see cref="NavigationManager"/>.
    /// </para>
    /// <para>
    /// <b>Called by the registration, as the gateway is built, and the timing is the point.</b>
    /// <see cref="NavigationManager.LocationChanged"/> runs its handlers in the order they subscribed, and the
    /// project card re-reads the revision from its own handler. Every component that reads through this gateway
    /// has it injected before its own <c>OnInitialized</c> can subscribe, so subscribing here, at construction,
    /// puts the invalidation ahead of every re-read it has to precede.
    /// </para>
    /// </remarks>
    /// <param name="navigation">This circuit's navigation.</param>
    public void FollowNavigation(NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        Dispose();
        _navigation = navigation;
        _navigation.LocationChanged += OnLocationChanged;
    }

    /// <summary>Stops following the circuit's navigation.</summary>
    public void Dispose()
    {
        if (_navigation is not null)
        {
            _navigation.LocationChanged -= OnLocationChanged;
            _navigation = null;
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) => Invalidate();

    /// <summary>Drops every cached read.</summary>
    /// <remarks>
    /// <c>info</c> is dropped too, because it now carries the AI connection — which a save from the settings
    /// screen changes, and a cache that outlived the save would report "not configured" to the operator who
    /// had just configured it.
    /// </remarks>
    public void Invalidate()
    {
        _descriptor = null;
        _schema = null;
        _capabilities = null;
        _info = null;
    }

    /// <summary>
    /// Runs one management call as the signed-in operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of the dashboard's authorization story, and it is deliberately not its
    /// own.</b> The management service reads its caller from <see cref="IAlvoContextAccessor"/> —
    /// the same ambient principal the HTTP route filter publishes before every management request.
    /// Publishing it here rather than checking a level inside a component is what makes <i>"the
    /// dashboard is not a second authorization system"</i> true by construction: the screen asks,
    /// the engine refuses, and the refusal arrives as the same exception an HTTP caller gets.
    /// </para>
    /// <para>
    /// <b>Per call rather than once per circuit.</b> A Blazor event handler runs on its own
    /// execution context, so an ambient value set at circuit start is not reliably still there
    /// when a button is clicked twenty minutes later. The principal itself is resolved once and
    /// cached; what repeats is only publishing it.
    /// </para>
    /// <para>
    /// The previous value is restored rather than cleared, because during a statically rendered
    /// pass this runs inside an HTTP request that may already have published one.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the call answers with.</typeparam>
    /// <param name="call">The management call.</param>
    /// <returns>Whatever the call answered.</returns>
    /// <param name="ct">Cancels resolving the caller; the call itself carries its own.</param>
    private async Task<T> AsOperatorAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        var previous = ambient.Principal;
        ambient.Principal = await CallerAsync(ct).ConfigureAwait(false);

        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            ambient.Principal = previous;
        }
    }

    /// <summary>
    /// The operator's caller, re-resolved before every call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not cached, and this is the one place in the gateway where that rule is
    /// reversed.</b> Every read above caches because a stale descriptor is visible and safe. A
    /// stale <em>caller</em> is neither: a Blazor Server scope is the circuit, so a principal
    /// resolved once at sign-in would survive for as long as the tab stays open. An operator who is
    /// disabled, stripped of their roles or has their tenant revoked would keep the authority they
    /// had at sign-in, while the very same person over <c>/api</c> is refused on the next request
    /// — and §3.7 argues <c>SetDisabledAsync</c> as a real lockout, which it would not be.
    /// </para>
    /// <para>
    /// The cost is one indexed read of the membership store per management call, against a screen
    /// that already makes a network round trip to serve the click. That is the right side of the
    /// trade: the cached version buys microseconds and sells the revocation story.
    /// </para>
    /// <para>
    /// A <see langword="null"/> answer is left as <see langword="null"/>: the core then sees an
    /// unauthenticated call and refuses it with <see cref="ManagementForbiddenException"/>, which
    /// is the screen an operator whose account was disabled mid-session should be looking at.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancels the read of the membership store.</param>
    private async ValueTask<AlvoPrincipal?> CallerAsync(CancellationToken ct)
    {
        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        return await callers.ResolveAsync(state.User, ct).ConfigureAwait(false);
    }

    private async ValueTask<string> ProjectAsync(CancellationToken ct)
    {
        if (Project.Length > 0)
        {
            return Project;
        }

        var projects = await ProjectsAsync(ct).ConfigureAwait(false);
        Project = projects.Count > 0 ? projects[0].Name : string.Empty;
        return Project;
    }
}
