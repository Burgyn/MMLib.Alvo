using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using System.Globalization;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// One minimal-API delegate per <see cref="IAlvoManagement"/> member, mapped under the configured prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every delegate is a thin adapter over the same service the dashboard calls in-process</b> — it binds,
/// calls one member, and renders. No business rule lives here, which is what keeps the two transports on one
/// path.
/// </para>
/// <para>
/// <b>Every route carries the same two filters, in this order:</b> <see cref="ManagementCallerFilter"/>
/// resolves the presented credential and publishes the caller, and
/// <c>RequireAlvoManagementAccess</c> refuses unless the descriptor's <c>access</c> block admits them to the
/// operation. The order is asserted rather than assumed — the gate reads what the first filter published,
/// so a reversed pair would judge every caller as anonymous.
/// </para>
/// <para>
/// <b>The routes are excluded from the OpenAPI document</b> (<c>ExcludeFromDescription</c>), deliberately
/// and temporarily: the document Alvo publishes is the <em>generated</em> Data API contract, pinned by
/// <c>OpenApiDocumentTests.The_document_is_stable</c>, linted by <c>scripts/lint-api</c> and pinned again by
/// the TeaPie e2e suite as a path-set equality. Mixing a hand-written admin surface into it would move all
/// three for a reason that has nothing to do with the Data API. A document of its own is the follow-on.
/// </para>
/// </remarks>
internal static class ManagementEndpoints
{
    /// <summary>Maps every management route under <paramref name="options"/>' prefix.</summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="options">The management options the prefix is read from.</param>
    /// <returns>The group the routes were mapped into.</returns>
    internal static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints, AlvoManagementOptions options)
    {
        var group = endpoints.MapGroup(RoutePrefix.Normalize(options.RoutePrefix));
        group.ExcludeFromDescription();

        MapInfo(group);
        MapProjects(group);
        MapDescriptorRead(group);
        MapRevisions(group);
        MapRevision(group);
        MapSchema(group);
        MapCapabilities(group);
        MapPolicySimulation(group);
        MapApply(group);

        return group;
    }

    /// <summary><c>GET {prefix}/info</c> — <see cref="IAlvoManagement.GetInfoAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapInfo(RouteGroupBuilder group) =>
        Gate(
            group.MapGet("/info", (IAlvoManagement management, CancellationToken ct) => management.GetInfoAsync(ct)),
            new ManagementRoute(nameof(IAlvoManagement.GetInfoAsync), ManagementOperation.GetInfo));

    /// <summary><c>GET {prefix}/projects</c> — <see cref="IAlvoManagement.ListProjectsAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapProjects(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects",
                (IAlvoManagement management, CancellationToken ct) => management.ListProjectsAsync(ct)),
            new ManagementRoute(nameof(IAlvoManagement.ListProjectsAsync), ManagementOperation.ListProjects));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/descriptor</c> — <see cref="IAlvoManagement.GetDescriptorAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapDescriptorRead(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/descriptor",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetDescriptorAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetDescriptorAsync), ManagementOperation.GetDescriptor));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/revisions</c> — <see cref="IAlvoManagement.ListRevisionsAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapRevisions(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/revisions",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.ListRevisionsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.ListRevisionsAsync), ManagementOperation.ListRevisions));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/revisions/{revision}</c> —
    /// <see cref="IAlvoManagement.GetRevisionAsync"/>.
    /// </summary>
    /// <remarks>
    /// The <c>:int</c> constraint is what keeps a revision number out of the delegate's hands: a segment that
    /// is not a number never matches, so nothing here parses one and nothing has to decide what
    /// <c>revisions/latest</c> would mean.
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapRevision(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/revisions/{revision:int}",
                (string project, int revision, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetRevisionAsync(project, revision, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetRevisionAsync), ManagementOperation.GetRevision));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/schema</c> — <see cref="IAlvoManagement.GetSchemaAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapSchema(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/schema",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetSchemaAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetSchemaAsync), ManagementOperation.GetSchema));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/capabilities</c> —
    /// <see cref="IAlvoManagement.GetCapabilitiesAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapCapabilities(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/capabilities",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetCapabilitiesAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetCapabilitiesAsync), ManagementOperation.GetCapabilities));

    /// <summary>
    /// <c>POST {prefix}/projects/{project}/policy/simulate</c> —
    /// <see cref="IAlvoManagement.SimulatePolicyAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>POST</c> and not <c>GET</c>, and it writes nothing.</b> The question carries a caller with
    /// roles, which is a body rather than a query string; the verb describes the request's shape, and the
    /// level it needs is <c>Viewer</c> precisely because nothing is written.
    /// </para>
    /// <para>
    /// <b>The body is optional at binding.</b> A required one would be refused by the framework with a 400
    /// <em>before</em> either filter ran, so a caller the access block admits nobody from could tell a
    /// mapped route from an unmapped one by the status they got. Nullable, the gate decides first and the
    /// service answers 422 for a body it cannot use.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapPolicySimulation(RouteGroupBuilder group) =>
        Gate(
            group.MapPost(
                "/projects/{project}/policy/simulate",
                (string project,
                    ManagementPolicySimulation? simulation,
                    IAlvoManagement management,
                    CancellationToken ct) =>
                    Answer(() => management.SimulatePolicyAsync(project, simulation!, ct))),
            new ManagementRoute(nameof(IAlvoManagement.SimulatePolicyAsync), ManagementOperation.SimulatePolicy));

    /// <summary>
    /// <c>PUT {prefix}/projects/{project}/descriptor</c> — <see cref="IAlvoManagement.ApplyDescriptorAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one write path to a project's configuration</b>, at the same address its export is read from:
    /// spec §0.5 contract 4 forbids a second route, and <c>PUT</c> on the resource whose representation is
    /// being replaced is what RFC 9110 §9.3.4 already means.
    /// </para>
    /// <para>
    /// <b>The body binds as nullable</b>, for <see cref="MapPolicySimulation"/>'s reason: a required one is
    /// refused by the framework with a 400 <em>before</em> either filter runs, so a caller the access block
    /// admits nobody from could tell a mapped route from an unmapped one by the status they got.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapApply(RouteGroupBuilder group) =>
        Gate(
            group.MapPut(
                "/projects/{project}/descriptor",
                (string project,
                    ManagementApplyBody? body,
                    HttpRequest request,
                    IAlvoManagement management,
                    ManagementAccessEvaluator access,
                    IAlvoContextAccessor callers,
                    CancellationToken ct) =>
                    ApplyAsync(project, body, request, management, access, callers, ct)),
            new ManagementRoute(
                nameof(IAlvoManagement.ApplyDescriptorAsync), ManagementOperation.ApplyDescriptor));

    /// <summary>
    /// Reads the precondition and the dry-run flag, and refuses before anything is applied when either is
    /// missing or unreadable.
    /// </summary>
    /// <remarks>
    /// <b>Every precondition this API cannot evaluate is refused, never ignored</b> — the Data API's own
    /// rule, and the reason the three arms below are three different statuses. An absent <c>If-Match</c>
    /// answered as "revision 0" would be a lost update; a <c>?dryRun=</c> value this API cannot read,
    /// answered as "not a dry run", would commit the very schema change the caller asked to preview.
    /// </remarks>
    /// <param name="project">The project to apply to.</param>
    /// <param name="body">The request body, or <see langword="null"/> when none was sent.</param>
    /// <param name="request">The request, read for <c>If-Match</c> and <c>?dryRun=</c>.</param>
    /// <param name="management">The contract member's implementation.</param>
    /// <param name="access">The gate that resolves the caller's management level.</param>
    /// <param name="callers">Where the caller resolved for this request is published.</param>
    /// <param name="ct">Cancellation token.</param>
    private static Task<IResult> ApplyAsync(
        string project,
        ManagementApplyBody? body,
        HttpRequest request,
        IAlvoManagement management,
        ManagementAccessEvaluator access,
        IAlvoContextAccessor callers,
        CancellationToken ct)
    {
        var expected = Revision(request);
        if (!expected.Present)
        {
            return Refused(ProblemResultFactory.PreconditionRequired(IfMatchRequired));
        }

        if (expected.Value is not { } revision)
        {
            return Refused(ProblemResultFactory.PreconditionFailed(IfMatchUncomparable));
        }

        if (DryRunAsked(request) is not { } planOnly)
        {
            return Refused(ProblemResultFactory.ManagementValidation(DryRunUnreadable));
        }

        return string.IsNullOrWhiteSpace(body?.DescriptorJson)
            ? Refused(ProblemResultFactory.ManagementValidation(BodyRequired))
            : Answer(() => AdmittedApplyAsync(
                project, body.ToRequest(revision, planOnly), management, access, callers, ct));
    }

    /// <summary>
    /// Applies, after refusing a caller who may not change <b>who reaches the project</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spec §3.3:</b> <i>"<c>developer</c> edits what the backend is, <c>admin</c> also decides who may
    /// reach it."</i> The route's own gate is <c>ApplyDescriptor</c> at <c>Developer</c>, which is right for
    /// every block but one: <c>access</c> is inside the descriptor and every accepted apply re-primes the
    /// catalog the gate reads, so without this a <c>developer</c> promotes itself to <c>admin</c> by editing
    /// three lines of JSON. The level is re-resolved here rather than read off the route, because the
    /// requirement depends on what was sent.
    /// </para>
    /// <para>
    /// <b>A dry run is refused identically.</b> A preview writes nothing and cannot escalate on its own, but
    /// a preview whose plan the apply would then refuse tells an editor its change is ready when it is not —
    /// the same reason the destructive guardrail runs on both branches.
    /// </para>
    /// <para>
    /// It reads the applied descriptor through the contract, so an unknown project is
    /// <see cref="ManagementProjectNotFoundException"/> — the named 404 — before any level is resolved, and
    /// this route cannot answer a question about a project the others would refuse.
    /// </para>
    /// </remarks>
    /// <param name="project">The project to apply to.</param>
    /// <param name="apply">What the caller asked for.</param>
    /// <param name="management">The contract member's implementation.</param>
    /// <param name="access">The gate that resolves the caller's management level.</param>
    /// <param name="callers">Where the caller resolved for this request is published.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ManagementEscalationException">
    /// The apply would change the <c>access</c> block and the caller is not an administrator.
    /// </exception>
    private static async Task<ManagementApplyResult> AdmittedApplyAsync(
        string project,
        ManagementApplyRequest apply,
        IAlvoManagement management,
        ManagementAccessEvaluator access,
        IAlvoContextAccessor callers,
        CancellationToken ct)
    {
        var applied = await management.GetDescriptorAsync(project, ct).ConfigureAwait(false);
        if (ManagementAccessChange.Differs(applied.DescriptorJson, apply.DescriptorJson)
            && !access.Allows(ManagementLevel.Admin, callers.Principal?.Context ?? AlvoContext.Anonymous))
        {
            throw new ManagementEscalationException();
        }

        return await management.ApplyDescriptorAsync(project, apply, ct).ConfigureAwait(false);
    }

    /// <summary>One already-decided refusal, as the completed task a route handler answers with.</summary>
    /// <param name="problem">The problem document to answer.</param>
    private static Task<IResult> Refused(IResult problem) => Task.FromResult(problem);

    /// <summary>
    /// The revision an <c>If-Match</c> names, or which of the two ways it names none.
    /// </summary>
    /// <remarks>
    /// <b>A strong tag over an integer, and nothing else.</b> A weak tag is by definition not usable for a
    /// write precondition (RFC 9110 §8.8.3), <c>*</c> means "any current representation" — which no revision
    /// comparison can honour — and a list of tags names more than one. All three are
    /// <see cref="IfMatchRevision.Uncomparable"/> rather than ignored.
    /// </remarks>
    /// <param name="request">The request to read the header from.</param>
    internal static IfMatchRevision Revision(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var values) || values.Count == 0)
        {
            return IfMatchRevision.Absent;
        }

        var single = values.Count == 1 ? values[0] : null;

        return single is not null && !single.StartsWith(WeakTagPrefix, StringComparison.Ordinal)
            && int.TryParse(single.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? IfMatchRevision.Of(revision)
            : IfMatchRevision.Uncomparable;
    }

    /// <summary>
    /// Whether <c>?dryRun=</c> asked for a plan-only pass, or <see langword="null"/> when it carried a value
    /// this API cannot read.
    /// </summary>
    /// <remarks>
    /// <b>An unreadable value is refused rather than read as <see langword="false"/>.</b> A caller who wrote
    /// <c>?dryRun=yes</c> asked for a preview, and answering that with a committed schema change is the one
    /// outcome a dry run exists to make impossible. A bare <c>?dryRun</c> is unreadable too, deliberately:
    /// the allowance is explicit or it is not given.
    /// </remarks>
    /// <param name="request">The request to read the query string from.</param>
    private static bool? DryRunAsked(HttpRequest request) =>
        !request.Query.TryGetValue(DryRunKey, out var values)
            ? false
            : values.Count == 1 && bool.TryParse(values[0], out var asked) ? asked : null;

    /// <summary>The query-string key that asks for a plan-only pass.</summary>
    private const string DryRunKey = "dryRun";

    /// <summary>How a weak entity tag is introduced (RFC 9110 §8.8.3).</summary>
    private const string WeakTagPrefix = "W/";

    /// <summary>What a caller who sent no precondition has to do.</summary>
    private const string IfMatchRequired =
        "This write requires 'If-Match' carrying the descriptor's current revision, e.g. If-Match: \"3\". "
        + "Read that revision from GET the same path. Applying without one is a lost update nothing "
        + "would detect.";

    /// <summary>What a caller whose precondition names no revision has to do.</summary>
    private const string IfMatchUncomparable =
        "'If-Match' must carry the descriptor's revision as a single strong tag, e.g. If-Match: \"3\". "
        + "A weak tag, a list of tags and '*' name no revision this API can compare.";

    /// <summary>What a caller whose dry-run flag could not be read has to do.</summary>
    private const string DryRunUnreadable =
        "'dryRun' takes 'true' or 'false'. It is refused rather than assumed, because reading an "
        + "unrecognised value as 'false' would apply the change a caller asked to preview.";

    /// <summary>What a caller who sent no descriptor has to do.</summary>
    private const string BodyRequired =
        "An apply needs a JSON body carrying 'descriptorJson'. Send the descriptor to apply; "
        + "'allowDestructive', 'author' and 'reason' are optional.";

    /// <summary>
    /// Runs one contract member and turns its refusals into problem documents.
    /// </summary>
    /// <remarks>
    /// Every delegate that can be refused goes through here, so no endpoint decides a status of its own and
    /// every management refusal is minted through <see cref="ProblemResultFactory"/>'s one catalogue.
    /// </remarks>
    /// <typeparam name="T">What the member answers with.</typeparam>
    /// <param name="operation">The member to run.</param>
    private static async Task<IResult> Answer<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation().ConfigureAwait(false));
        }
        catch (ManagementProjectNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
        catch (ManagementRevisionNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
        catch (ManagementSimulationException refusal)
        {
            return ProblemResultFactory.ManagementValidation(refusal.Message);
        }
        catch (ManagementEscalationException)
        {
            return ProblemResultFactory.ManagementAccessChangeForbidden();
        }
        catch (DescriptorValidationException refusal)
        {
            return ProblemResultFactory.ManagementDescriptorRefused(refusal);
        }
        catch (DescriptorConcurrencyException refusal)
        {
            return ProblemResultFactory.PreconditionFailed(refusal.Message);
        }
        catch (DestructiveChangeNotAllowedException refusal)
        {
            return ProblemResultFactory.DestructiveChange(
                refusal.Message
                + " Send 'allowDestructive': true to proceed, or change the descriptor to keep what the "
                + "plan would drop.");
        }
    }

    /// <summary>Attaches the caller filter, the access gate and the metadata the contract test reads.</summary>
    /// <param name="route">The route being built.</param>
    /// <param name="operation">What the route stands for.</param>
    private static RouteHandlerBuilder Gate(RouteHandlerBuilder route, ManagementRoute operation) =>
        route.AddEndpointFilter<RouteHandlerBuilder, ManagementCallerFilter>()
            .RequireAlvoManagementAccess(operation.Operation)
            .WithMetadata(operation)
            .WithName($"Alvo.Management.{operation.Member}");
}
