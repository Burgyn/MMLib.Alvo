using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The gate every management route carries, measured over the wire rather than over the filter.
/// </summary>
/// <remarks>
/// <b>Default-deny is the shipped answer, and it is a property of the descriptor rather than of a stub.</b>
/// A project that declares no <c>access</c> block names nobody, so nobody but the deployment's bootstrap
/// administrator is admitted — which is what the first three facts measure. The fourth applies a descriptor
/// that does name somebody, which is the only way past the gate and the only way the ordering of the two
/// filters is observable: the gate judges whoever the credential resolved to, so a 200 is also the fact that
/// authentication ran first.
/// </remarks>
public class ManagementAccessTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>
    /// The same roles as <see cref="_ops"/>, and a scope grant on one entity this project does not even
    /// declare — the narrowest credential that can still be issued.
    /// </summary>
    private static readonly TestApiKey _narrow = new("mgmt-narrow", ["ops"], ["orders:read"]);

    /// <summary>
    /// <b>Every route the table carries, not the one path this suite remembers.</b> The contract facts
    /// cannot see this — they read metadata, and metadata is attached by a different call — so the live
    /// endpoint table is swept and every address is asked the one question the surface owes: does it refuse
    /// a caller the project names nowhere?
    /// </summary>
    /// <remarks>
    /// <b>It discriminates admission, not which of the two gates supplies it.</b> Both do: the route filter
    /// refuses before model binding, and <c>AlvoManagementService</c> refuses at the head of the member it
    /// calls. So stripping <c>RequireAlvoManagementAccess</c> from a route leaves this green —
    /// <c>ManagementEndpoints.Answer</c> renders the service's <c>ManagementForbiddenException</c> as the
    /// identical 403, which is
    /// <see cref="The_contracts_own_refusal_is_rendered_rather_than_escaping_as_a_fault"/>'s subject. What
    /// turns it red is a route that reaches neither: one mapped outside <c>ManagementEndpoints.Gate</c>
    /// onto something that is not a contract member.
    /// </remarks>
    [Fact]
    public async Task Every_mapped_management_route_refuses_a_caller_the_project_names_nowhere()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true));

        var addresses = world.ManagementRouteEndpoints().Select(AlvoApiWorld.AddressOf).ToList();

        addresses.ShouldNotBeEmpty("a sweep over an empty route table proves nothing about any route");
        foreach (var (method, path) in addresses)
        {
            var response = await world.SendAsync(method, path, _admin);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path} is not gated");
        }
    }

    [Fact]
    public async Task A_project_that_names_nobody_refuses_an_admin_key_through_the_one_catalogue()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.Forbidden,
            "a management refusal is minted through the one catalogue, not a management-only spelling");
    }

    [Fact]
    public async Task An_anonymous_caller_is_403_rather_than_401()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info");

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "nothing failed authentication: no credential was presented, and the access block refused");
    }

    [Fact]
    public async Task A_presented_credential_that_cannot_be_used_is_401_before_the_gate_is_consulted()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json",
            [_ops],
            new AlvoApiWorldSetup(RevokedKeyId: _ops.KeyId, MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "this key would have been admitted, so the 401 can only come from the credential itself");
    }

    /// <summary>
    /// A management 401 carries the same <c>WWW-Authenticate</c> challenge a Data API 401 does.
    /// </summary>
    /// <remarks>
    /// RFC 7235 §3.1 makes the header a <b>MUST</b> on a 401, and the review of the first management batch
    /// recorded the opposite — "no <c>WWW-Authenticate</c> on the management surface" — which
    /// <c>docs/architecture/management-api.md</c> would have published as fact. It is measured here instead:
    /// the refusal comes from the shared <c>ProblemResultFactory.Unauthenticated</c>, so the challenge is
    /// the Data API's and naming the scheme is what lets an agent discover how to authenticate.
    /// </remarks>
    [Fact]
    public async Task A_management_401_names_the_scheme_and_the_header_to_send()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json",
            [_ops],
            new AlvoApiWorldSetup(RevokedKeyId: _ops.KeyId, MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe(
            $"AlvoApiKey header=\"{world.CredentialHeaderName}\"",
            "the name says both halves: a scheme with no auth-param leaves an agent knowing it needs a key "
            + "and not where to put it, and a host that moved the header would advertise the wrong one");
    }

    /// <summary>
    /// Two credential headers are an ambiguous credential, and an ambiguous credential is refused rather
    /// than disambiguated by taking whichever copy arrived first.
    /// </summary>
    /// <remarks>
    /// The rule is the Data API's, and it is a security rule rather than a parsing convenience — so it is
    /// measured on a management route too, not inherited by assertion. The key presented here would be
    /// admitted if it arrived once.
    /// </remarks>
    [Fact]
    public async Task An_ambiguous_credential_is_refused_rather_than_disambiguated()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json", [_ops], new AlvoApiWorldSetup(MapManagementApi: true));

        var header = world.CredentialHeaderName;
        var response = await world.SendRawAsync(
            HttpMethod.Get,
            "/management/info",
            headers:
            [
                new(header, _ops.Presented),
                new(header, "someone-elses-key.and-its-secret-long-enough"),
            ]);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized, "picking the first copy would admit whoever sends their header first");
    }

    [Fact]
    public async Task A_caller_the_access_block_names_reaches_info()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json", [_ops], new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonObjectAsync())["mode"]!.GetValue<string>().ShouldBe("standalone");
    }

    /// <summary>
    /// <b>The contract's own refusal is rendered as the same 403</b>, on the route that used to call the
    /// member without rendering anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes "the route filter is a cost optimisation, not the gate" a measured statement
    /// rather than a reading of two call sites. A fact cannot strip the filter off a route, so what the
    /// filter would otherwise hide is injected instead: an <see cref="IAlvoManagement"/> that refuses the
    /// way the shipped one refuses a caller no level admits, reached by a caller the <c>access</c> block
    /// <em>does</em> admit. The 403 therefore comes from the delegate's rendering of
    /// <see cref="ManagementForbiddenException"/> and from nothing else.
    /// </para>
    /// <para>
    /// <b><c>/info</c>, specifically.</b> It and <c>/projects</c> were the two routes that called their
    /// member outside <c>ManagementEndpoints.Answer</c>, so an exception raised inside them met no
    /// <c>catch</c> — and <c>AlvoExceptionHandler</c> answers only for endpoints carrying
    /// <c>DataApiOperationMetadata</c>, which a management route does not carry. The wire answer was a
    /// fault, not a refusal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_contracts_own_refusal_is_rendered_rather_than_escaping_as_a_fault()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json",
            [_ops],
            new AlvoApiWorldSetup(
                MapManagementApi: true,
                ConfigureServices: services => services.AddSingleton<IAlvoManagement>(new RefusingManagement())));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "an unrendered ManagementForbiddenException is a fault on the wire, not a refusal");
        (await response.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.Forbidden,
            "the service's refusal is minted through the same catalogue the filter's is");
    }

    /// <summary>
    /// <b>A key's scopes do not govern configuration, and that is a decision rather than an oversight</b>
    /// (the F5 design's D7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>ApiKeyScope</c> is <c>&lt;entity|*&gt;:&lt;read|write&gt;</c>, so there is no spelling for "may
    /// manage this project" — and rather than invent one, the Management API leaves the whole question to
    /// the descriptor's <c>access</c> block, which is the surface an administrator already edits. The
    /// consequence is what this fact states plainly: a credential narrow enough to be refused by the Data
    /// API still reaches management on its <em>roles</em>.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, because either alone is satisfiable by an accident.</b> A 200 on
    /// management says nothing unless the same key is genuinely narrow, and the Data API's <c>out-of-scope</c>
    /// is what establishes that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_key_scoped_to_one_entity_still_reaches_management_because_scopes_do_not_govern_configuration()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json", [_narrow], new AlvoApiWorldSetup(MapManagementApi: true));

        var refused = await world.SendAsync(HttpMethod.Get, "/api/notes", _narrow);
        var admitted = await world.SendAsync(HttpMethod.Get, "/management/info", _narrow);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.OutOfScope, "this key's scopes really do exclude every entity this project has");
        admitted.StatusCode.ShouldBe(
            HttpStatusCode.OK, "management admission is decided by the access block, never by a key's scopes");
    }

    [Fact]
    public async Task Mapping_the_management_api_adds_no_route_under_the_data_api_prefix()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        (await world.SendAsync(HttpMethod.Get, "/api/info")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_that_does_not_map_the_management_api_serves_none_of_it()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("managed-notes.alvo.json", [_ops]);

        (await world.SendAsync(HttpMethod.Get, "/management/info", _ops)).StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "registering Alvo exposes nothing; the endpoint seam is a separate call");
    }
}

/// <summary>
/// An <see cref="IAlvoManagement"/> whose every member refuses the way the shipped one refuses a caller no
/// level admits.
/// </summary>
/// <remarks>
/// It exists because the shipped refusal is, on the HTTP transport, <b>unreachable past the route filter</b>
/// — the filter and the service read the same table through the same evaluator, so the filter always
/// answers first. Registered <em>before</em> <c>AddAlvo</c>, so the framework's own
/// <c>TryAddSingleton&lt;IAlvoManagement&gt;</c> leaves it in place, exactly as <c>FaultingAlvoData</c> is
/// registered for the failure family no well-formed request can reach.
/// </remarks>
internal sealed class RefusingManagement : IAlvoManagement
{
    /// <inheritdoc/>
    public Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(
        string project, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementRevisionDetail> GetRevisionAsync(
        string project, int revision, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<MMLib.Alvo.Schema.SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();

    /// <inheritdoc/>
    public Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default) =>
        throw new ManagementForbiddenException();
}
