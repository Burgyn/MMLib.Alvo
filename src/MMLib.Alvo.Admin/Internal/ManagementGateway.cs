using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
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
    private readonly Slot<ManagementDescriptor> _descriptor = new();
    private readonly Slot<SchemaModel> _schema = new();
    private readonly Slot<ManagementCapabilities> _capabilities = new();
    private readonly Slot<ManagementCelFunctions> _functions = new();
    private readonly Slot<ManagementInfo> _info = new();
    private readonly Slot<IReadOnlyList<ManagementProject>> _projects = new();
    private IDisposable? _following;
    private int _generation;

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
        return await CachedAsync(_descriptor, () => management.GetDescriptorAsync(project, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>The resolved schema — what the Data API actually serves.</summary>
    public async ValueTask<SchemaModel> SchemaAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await CachedAsync(_schema, () => management.GetSchemaAsync(project, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>What this build honours, warns about and refuses.</summary>
    public async ValueTask<ManagementCapabilities> CapabilitiesAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await CachedAsync(_capabilities, () => management.GetCapabilitiesAsync(project, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Every CEL function a descriptor may call on this instance — built-ins and the host's (C1 <c>cel/functions</c>).</summary>
    /// <remarks>
    /// Cached like capabilities. A failure to ask answers an empty list and caches nothing: the editor then simply offers
    /// no list, and a helper must never be the reason an operator cannot edit (as <see cref="CheckExpressionAsync"/>).
    /// </remarks>
    public async ValueTask<IReadOnlyList<CelFunctionInfo>> CelFunctionsAsync(CancellationToken ct)
    {
        try
        {
            var project = await ProjectAsync(ct).ConfigureAwait(false);
            var answer = await CachedAsync(_functions, () => management.GetCelFunctionsAsync(project, ct), ct)
                .ConfigureAwait(false);
            return answer.Functions;
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException
            or OperationCanceledException or HttpRequestException)
        {
            return [];
        }
    }

    /// <summary>Build, mode, data provider and startup mode.</summary>
    public ValueTask<ManagementInfo> InfoAsync(CancellationToken ct)
        => CachedAsync(_info, () => management.GetInfoAsync(ct), ct);

    /// <summary>Every project this build manages — one, today (§2.6).</summary>
    public ValueTask<IReadOnlyList<ManagementProject>> ProjectsAsync(CancellationToken ct)
        => CachedAsync(_projects, () => management.ListProjectsAsync(ct), ct);

    /// <summary>A cached read, or the call that fills it.</summary>
    /// <remarks>
    /// <b>The answer is kept only if no <see cref="Invalidate"/> ran while it was on its way.</b> A read that
    /// started before a navigation or an apply and finished after one carries the configuration as it was, and
    /// storing it would put back exactly the stale value the invalidation had just dropped — so the generation
    /// is taken before the await and compared after it. The caller still gets the answer it asked for.
    /// </remarks>
    private async ValueTask<T> CachedAsync<T>(Slot<T> slot, Func<Task<T>> call, CancellationToken ct)
        where T : class
    {
        if (slot.Value is { } cached)
        {
            return cached;
        }

        var generation = Volatile.Read(ref _generation);
        var fresh = await AsOperatorAsync(call, ct).ConfigureAwait(false);

        if (generation == Volatile.Read(ref _generation))
        {
            slot.Value = fresh;
        }

        return fresh;
    }

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

    /// <summary>
    /// Asks what apply would say about one expression, or <see langword="null"/> when the check itself could not be
    /// asked — a helper must never be the reason an operator cannot edit.
    /// </summary>
    /// <remarks>
    /// Never cached: the verdict depends on the working copy, which changes with every keystroke. Only the failures
    /// of asking are swallowed; any other exception is a bug and propagates.
    /// </remarks>
    public async Task<ManagementExpressionVerdict?> CheckExpressionAsync(
        string descriptorJson, string path, string source, CancellationToken ct)
    {
        try
        {
            var project = await ProjectAsync(ct).ConfigureAwait(false);
            var check = new ManagementExpressionCheck(descriptorJson, path, source);
            return await AsOperatorAsync(
                () => management.CheckExpressionAsync(project, check, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException
            or OperationCanceledException or HttpRequestException)
        {
            return null;
        }
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

    /// <summary>
    /// Grants and revokes the named roles against the person's roles <em>as stored now</em>, not as the screen
    /// last showed them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the screen sends a change and not a set.</b> The port's <c>SetRolesAsync</c> is a replacement, and
    /// the chip group's selection is computed from the row as it was loaded. Sending that set from a tab that
    /// was open while another administrator revoked a role would silently grant it back — the one direction a
    /// stale screen must never move authority (security review of <c>c10f75c</c>, Q1). So the screen names only
    /// what its operator pressed, and this reads the person fresh, applies that to the stored roles, and
    /// replaces with the result.
    /// </para>
    /// <para>
    /// <b>Done here, over the port's existing members, rather than as a grant/revoke pair on the port.</b> That
    /// would be two public members every implementation has to carry, for a problem only a screen that holds a
    /// snapshot has. What it does not buy is atomicity: between the fresh read and the replacement another write
    /// can still land, and the later one wins. That window is the length of one round trip rather than the life
    /// of a tab, and closing it is the port's expected-version follow-up (<c>docs/todo-admin.md</c> §8d item 37).
    /// </para>
    /// </remarks>
    /// <param name="shown">The person as the screen shows them — for the id and the address to find them by.</param>
    /// <param name="grant">The roles to add.</param>
    /// <param name="revoke">The roles to take away.</param>
    /// <param name="ct">A token to cancel the reads and the write.</param>
    /// <returns>The person as they now are.</returns>
    /// <exception cref="AlvoPreconditionFailedException">The person is no longer stored.</exception>
    public Task<AlvoUser> ChangeRolesAsync(
        AlvoUser shown, IReadOnlyCollection<string> grant, IReadOnlyCollection<string> revoke, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(revoke);

        return AsOperatorAsync(async () =>
        {
            var stored = await StoredAsync(shown, ct).ConfigureAwait(false);
            IReadOnlyList<string> next =
            [
                .. stored.RoleNames.Except(revoke, StringComparer.Ordinal),
                .. grant.Except(stored.RoleNames, StringComparer.Ordinal),
            ];
            return await Administration.SetRolesAsync(shown.Id, next, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>What an operator pressing a chip changed: the roles now selected that were not shown, and the reverse.</summary>
    /// <param name="shown">The roles the row showed.</param>
    /// <param name="selected">The roles the chip group now has selected.</param>
    /// <returns>The roles to grant and the roles to revoke.</returns>
    internal static (IReadOnlyList<string> Grant, IReadOnlyList<string> Revoke) RoleChange(
        IReadOnlyList<string> shown, IReadOnlyList<string> selected)
        => ([.. selected.Except(shown, StringComparer.Ordinal)], [.. shown.Except(selected, StringComparer.Ordinal)]);

    /// <summary>Reads a person as the store holds them now, through the port's own paged read.</summary>
    /// <remarks>
    /// The port has no read by id, so this searches by the person's address and matches the id, following the
    /// cursor: the search is a substring match, so another address can contain this one.
    /// </remarks>
    /// <param name="shown">The person as the screen shows them.</param>
    /// <param name="ct">A token to cancel the reads.</param>
    /// <returns>The stored person.</returns>
    /// <exception cref="AlvoPreconditionFailedException">The person is no longer stored.</exception>
    private async Task<AlvoUser> StoredAsync(AlvoUser shown, CancellationToken ct)
    {
        string? after = null;
        do
        {
            var page = await Administration.ListAsync(new AlvoUserQuery(shown.Email, 200, after), ct)
                .ConfigureAwait(false);
            if (page.Users.FirstOrDefault(person => person.Id == shown.Id) is { } stored)
            {
                return stored;
            }

            after = page.NextCursor;
        }
        while (after is not null);

        throw new AlvoPreconditionFailedException(
            "This person is no longer there — somebody removed them while this screen was open. Reload the list.");
    }

    /// <summary>Grants, changes or removes the one tenant a person acts in.</summary>
    public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetTenantAsync(user, tenant, ct), ct);

    /// <summary>Bars a person from signing in, or lets them back.</summary>
    public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetDisabledAsync(user, disabled, ct), ct);

    /// <summary>Ends a person's temporary lockout from failed sign-ins now.</summary>
    public Task<AlvoUser> ClearLockoutAsync(UserId user, CancellationToken ct)
        => AsOperatorAsync(() => Administration.ClearLockoutAsync(user, ct), ct);

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
    /// Runs a whole stream as the signed-in operator: every step of it, and nothing between two steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Published before every step rather than once, and the difference is load-bearing.</b>
    /// <c>IAlvoContextAccessor</c> is an <see cref="System.Threading.AsyncLocal{T}"/> holder. An async iterator
    /// resumes on its <em>consumer's</em> execution context each time the consumer asks for the next item, so a
    /// caller published once at the top of this body was visible to the first step alone. An assistant turn
    /// calls its tools from the second step on — after the model's first update — so every tool saw no
    /// principal, resolved to <c>AlvoContext.Anonymous</c> and was refused: an admin was told the assistant had
    /// no permission to read the schema (reported 24 Sep 2026). The enumerator is therefore driven by hand, and
    /// its creation, each <c>MoveNextAsync</c> and its disposal each run inside
    /// <see cref="AsCallerAsync{TResult}(AlvoPrincipal?, Func{ValueTask{TResult}})"/>.
    /// </para>
    /// <para>
    /// <b>Restored after every step, so the operator's authority stays inside the stream's own work.</b> What
    /// the consumer does with an update runs as whoever it already was, and work a step left running stops
    /// seeing the operator once that step has ended — restoring clears the holder the step's context captured.
    /// </para>
    /// <para>
    /// The caller is resolved once per stream, not per step: a turn is one request from the operator, and a
    /// membership read between two tokens of an answer would buy nothing the next turn does not.
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

        var caller = await CallerAsync(ct).ConfigureAwait(false);
        var inner = await AsCallerAsync(caller, () => ValueTask.FromResult(stream().GetAsyncEnumerator(ct)))
            .ConfigureAwait(false);

        try
        {
            while (await AsCallerAsync(caller, inner.MoveNextAsync).ConfigureAwait(false))
            {
                yield return inner.Current;
            }
        }
        finally
        {
            await AsCallerAsync(caller, async () =>
            {
                await inner.DisposeAsync().ConfigureAwait(false);

                return true;
            }).ConfigureAwait(false);
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
    /// <b>On the location <em>changing</em>, not changed, and the order is the point.</b> The router subscribes
    /// to <see cref="NavigationManager.LocationChanged"/> before this gateway exists and renders the destination
    /// page inside its own handler, so an invalidation hung on the same event ran after the new page had already
    /// read the old cache. A location-changing handler runs before any <c>LocationChanged</c> handler — for a
    /// link click, a <c>NavigateTo</c> (a query-only one too) and a step back or forward alike — so every
    /// screen, and the project card, reads after the drop. It never cancels a navigation.
    /// </para>
    /// <para>
    /// <b>A statically rendered pass has nothing to follow</b>: its navigation manager supports no
    /// location-changing handlers, and its scope is one request that never navigates. The gateway then caches
    /// for that request alone, which is what it did before. The registration throws after the handler has been
    /// added, so the handler stays on that request's navigation manager with no registration to dispose — which
    /// is harmless: the manager lives for the one request, and nothing there ever raises it.
    /// </para>
    /// <para>
    /// The cost, per navigation of an interactive circuit:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A re-read of these four — including the query-only navigations, an entity's tab or
    /// the grid's filter — each an in-process call rather than a network round trip.</description></item>
    /// <item><description>One circuit round trip before the browser commits the navigation. A registered
    /// handler is a navigation lock to the framework, so the browser asks the server first, where it used to
    /// navigate and tell it afterwards.</description></item>
    /// <item><description>A replayed step for back and forward. The browser has already moved through its
    /// history when the lock hears of it, so the framework steps back to where it was, asks the handler, and
    /// then repeats the step.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="navigation">This scope's navigation, initialised.</param>
    public void FollowNavigation(NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        Unfollow();
        try
        {
            _following = navigation.RegisterLocationChangingHandler(OnLocationChanging);
        }
        catch (NotSupportedException)
        {
            _following = null;
        }
    }

    /// <summary>Stops following the scope's navigation, when the scope ends.</summary>
    public void Dispose() => Unfollow();

    private void Unfollow()
    {
        _following?.Dispose();
        _following = null;
    }

    private ValueTask OnLocationChanging(LocationChangingContext context)
    {
        Invalidate();
        return ValueTask.CompletedTask;
    }

    /// <summary>Drops every cached read.</summary>
    /// <remarks>
    /// <c>info</c> is dropped too, because it now carries the AI connection — which a save from the settings
    /// screen changes, and a cache that outlived the save would report "not configured" to the operator who
    /// had just configured it. The project list is kept, as it always was; the project card reads the current
    /// revision from the descriptor rather than from it.
    /// </remarks>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _descriptor.Value = null;
        _schema.Value = null;
        _capabilities.Value = null;
        _functions.Value = null;
        _info.Value = null;
    }

    /// <summary>One cached read. A class, so <see cref="CachedAsync{T}"/> can fill it across an await.</summary>
    private sealed class Slot<T>
        where T : class
    {
        public T? Value { get; set; }
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
    /// The previous value is restored rather than cleared, but that is not the same as nesting safely under
    /// an outer publication. The core accessor's setter always <em>nulls the box the previous value lives
    /// in</em>, whether or not it is about to install a new one, so a scope that had already published a
    /// principal before reaching here — an HTTP request during a statically rendered pass, say — loses it the
    /// instant the first assignment below runs, and the restore in the <c>finally</c> never gets it back: it
    /// builds a fresh box the outer scope was never pointed at. Nesting two calls through this accessor is
    /// therefore unsupported, not merely untested — it fails safe (to no principal) rather than silently —
    /// and <c>docs/todo-admin.md</c> §8d tracks the core-side fix.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the call answers with.</typeparam>
    /// <param name="call">The management call.</param>
    /// <returns>Whatever the call answered.</returns>
    /// <param name="ct">Cancels resolving the caller; the call itself carries its own.</param>
    private async Task<T> AsOperatorAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        var caller = await CallerAsync(ct).ConfigureAwait(false);

        return await AsCallerAsync(caller, () => new ValueTask<T>(call())).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one step with <paramref name="caller"/> published, and restores what was published before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An async method of its own, so the publication cannot outlive the step.</b> The step is started inside
    /// this method, so it and every continuation it awaits capture the caller; this method's own context is
    /// discarded when it returns, so its caller never sees the assignment at all.
    /// </para>
    /// <para>
    /// The restore is still needed, and not for the method's caller: it clears the holder the step's context
    /// captured, so work the step started and left running stops seeing the operator. It restores the
    /// previous value rather than clearing it outright, but that is not real nesting support: the accessor's
    /// setter nulls the box a value lives in on every assignment, so a caller that had already published a
    /// principal before reaching this method loses it the moment <paramref name="caller"/> is published below,
    /// and the <c>finally</c> hands the current context a new box rather than the outer scope's own —
    /// <c>docs/todo-admin.md</c> §8d tracks the core-side fix this is waiting on.
    /// </para>
    /// </remarks>
    /// <typeparam name="TResult">What the step answers with.</typeparam>
    /// <param name="caller">Who the step runs as; <see langword="null"/> leaves the core to refuse it.</param>
    /// <param name="step">The step.</param>
    private async ValueTask<TResult> AsCallerAsync<TResult>(AlvoPrincipal? caller, Func<ValueTask<TResult>> step)
    {
        var previous = ambient.Principal;
        ambient.Principal = caller;

        try
        {
            return await step().ConfigureAwait(false);
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
