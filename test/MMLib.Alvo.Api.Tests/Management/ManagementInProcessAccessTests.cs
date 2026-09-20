using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// Management authorization measured on the <b>in-process</b> transport — a caller holding
/// <see cref="IAlvoManagement"/> directly, with no HTTP request and no endpoint filter anywhere in the call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other access fact in this repository goes over HTTP, which is exactly why this was invisible.</b>
/// Both guards used to live in the HTTP adapter. <see cref="IAlvoManagement"/> is public and
/// <c>docs/architecture/management-api.md</c> says the dashboard calls it in-process — and a dashboard
/// resolves ONE registered instance and serves MANY humans through it, so "whatever composed this reference
/// already admitted the caller" admits the <em>process</em>, not the person. A guard on one transport only
/// is the divergent authorization path spec §0.5 contract 4 forbids.
/// </para>
/// <para>
/// <b>Both halves are measured here, because both are per-request.</b> The level table
/// (<c>ManagementOperations</c>) answers "may this caller apply at all"; the §3.3 comparison answers "may
/// this caller change who may reach the project". The first was left in the adapter once, on the reading
/// that a level is settled at composition — the same premise this suite's existence debunks, and the gap it
/// left was a <c>viewer</c> applying a descriptor in-process whose <c>rules</c> block granted itself the
/// whole Data API.
/// </para>
/// </remarks>
public class ManagementInProcessAccessTests
{
    /// <summary>A caller the project's <c>access</c> block admits at <c>developer</c>.</summary>
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

    /// <summary>A caller the same block admits at <c>admin</c>.</summary>
    private static readonly TestApiKey _owner = new("mgmt-owner", ["owner"], ["*:write"]);

    /// <summary>A caller the same block admits at <c>viewer</c>, the level below every write.</summary>
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>
    /// A <c>developer</c> calling the contract member directly is refused exactly as one over HTTP is.
    /// </summary>
    [Fact]
    public async Task A_developer_calling_the_contract_in_process_may_not_change_who_may_reach_the_project()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var management = Publish(world, "dispatcher");
        var current = await management.GetDescriptorAsync(ManagedFleet.Project, Ct);

        await Should.ThrowAsync<ManagementEscalationException>(() => management.ApplyDescriptorAsync(
            ManagedFleet.Project,
            new ManagementApplyRequest(Escalated(current.DescriptorJson), current.Revision),
            Ct));

        (await management.GetDescriptorAsync(ManagedFleet.Project, Ct)).Revision.ShouldBe(
            1, "a refused apply appends nothing");
    }

    /// <summary>
    /// An <c>admin</c> calling in-process makes exactly the change the developer was refused.
    /// </summary>
    /// <remarks>
    /// Without this the guard would be indistinguishable from "the in-process transport may not edit
    /// <c>access</c> at all", which is not what §3.3 says and would break the dashboard the surface exists
    /// for.
    /// </remarks>
    [Fact]
    public async Task An_administrator_calling_the_contract_in_process_may_change_it()
    {
        await using var world = await ManagedFleet.StartAsync([_owner]);
        var management = Publish(world, "owner");
        var current = await management.GetDescriptorAsync(ManagedFleet.Project, Ct);

        var applied = await management.ApplyDescriptorAsync(
            ManagedFleet.Project,
            new ManagementApplyRequest(Escalated(current.DescriptorJson), current.Revision),
            Ct);

        applied.Applied.ShouldBeTrue();
    }

    /// <summary>The other direction: a <c>developer</c> still edits everything that is not <c>access</c>.</summary>
    [Fact]
    public async Task A_developer_calling_the_contract_in_process_still_applies_an_ordinary_change()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var management = Publish(world, "dispatcher");
        var current = await management.GetDescriptorAsync(ManagedFleet.Project, Ct);

        var applied = await management.ApplyDescriptorAsync(
            ManagedFleet.Project,
            new ManagementApplyRequest(
                DescriptorEdits.AddOptionalTextField(current.DescriptorJson, "vehicles", "nickname"),
                current.Revision),
            Ct);

        applied.Applied.ShouldBeTrue("the guard is about the access block, not about applying at all");
    }

    /// <summary>The rollback member carries the same guard on the same transport.</summary>
    /// <remarks>
    /// The history is built over HTTP by the administrator the project names, so revision 1 and revision 2
    /// differ in nothing but the <c>access</c> block — which makes the in-process restore of revision 1
    /// measurably an access change rather than a validation failure.
    /// </remarks>
    [Fact]
    public async Task A_developer_calling_rollback_in_process_may_not_restore_a_different_access_block()
    {
        await using var world = await ManagedFleet.StartAsync([_owner]);
        var reviewed = DescriptorEdits.GrantViewTo(
            await ManagementApplyWorld.CurrentAsync(world, _owner), role: "owner");
        (await ManagementApplyWorld.ApplyAsync(world, _owner, reviewed, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var management = Publish(world, "dispatcher");

        await Should.ThrowAsync<ManagementEscalationException>(() => management.RollbackAsync(
            ManagedFleet.Project, targetRevision: 1, new ManagementRollbackRequest(ExpectedRevision: 2), Ct));
    }

    /// <summary>
    /// A <c>viewer</c> may not apply a descriptor in-process, even one that leaves <c>access</c> alone.
    /// </summary>
    /// <remarks>
    /// <b>This is the fact the level gate exists for.</b> <c>ManagementOperations</c> reserves
    /// <c>ApplyDescriptor</c> to <c>developer</c>, and while that table was enforced by the endpoint filter
    /// alone, a dashboard serving a <c>viewer</c> could apply in-process as long as it did not touch
    /// <c>access</c> — and a rewritten <c>rules</c> block is full Data API read and write, which is the same
    /// escalation by another route. The edit here is the most ordinary one there is, so nothing but the
    /// level can explain the refusal.
    /// </remarks>
    [Fact]
    public async Task A_viewer_calling_the_contract_in_process_may_not_apply_at_all()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var reader = Publish(world, "ops");
        var current = await reader.GetDescriptorAsync(ManagedFleet.Project, Ct);

        await Should.ThrowAsync<ManagementForbiddenException>(() => reader.ApplyDescriptorAsync(
            ManagedFleet.Project,
            new ManagementApplyRequest(
                DescriptorEdits.AddOptionalTextField(current.DescriptorJson, "vehicles", "nickname"),
                current.Revision),
            Ct));

        (await reader.GetDescriptorAsync(ManagedFleet.Project, Ct)).Revision.ShouldBe(
            1, "a refused apply appends nothing");
    }

    /// <summary>A <c>viewer</c> may not roll back either, and reads the history perfectly well.</summary>
    /// <remarks>
    /// The two write members carry the same level, so measuring one would leave the other's gate to be
    /// assumed. The read in the same fact is what keeps the refusal about the operation rather than about
    /// the caller: the same reference, the same published principal, one member answers and one refuses.
    /// </remarks>
    [Fact]
    public async Task A_viewer_calling_the_contract_in_process_reads_the_history_and_may_not_restore_it()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var reader = Publish(world, "ops");

        (await reader.ListRevisionsAsync(ManagedFleet.Project, Ct)).ShouldNotBeEmpty();

        await Should.ThrowAsync<ManagementForbiddenException>(() => reader.RollbackAsync(
            ManagedFleet.Project, targetRevision: 1, new ManagementRollbackRequest(ExpectedRevision: 1), Ct));
    }

    /// <summary>
    /// An in-process caller with nothing published is anonymous, and anonymous reaches no level at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the composition a review would call "the dashboard forgot to publish who it is acting for".
    /// It fails closed, which is the only reading <c>ManagementAccessEvaluator</c> gives anywhere else.
    /// </para>
    /// <para>
    /// <b>The read refuses too, and that is the behaviour change worth stating.</b> Before the level gate
    /// moved behind the contract, an unattended in-process call could read anything and was refused only
    /// where it touched <c>access</c>. Now every member needs a published principal — which is what
    /// default-deny means once the surface admits it does not know who is calling.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_in_process_caller_who_published_nobody_is_refused()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var current = await Publish(world, "dispatcher").GetDescriptorAsync(ManagedFleet.Project, Ct);
        var unattended = Unpublish(world);

        await Should.ThrowAsync<ManagementForbiddenException>(
            () => unattended.GetDescriptorAsync(ManagedFleet.Project, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => unattended.ApplyDescriptorAsync(
            ManagedFleet.Project,
            new ManagementApplyRequest(Escalated(current.DescriptorJson), current.Revision),
            Ct));
    }

    /// <summary>
    /// Publishes a caller holding <paramref name="roleName"/> and hands back the contract implementation.
    /// </summary>
    /// <remarks>
    /// The principal is published through the same <see cref="IAlvoContextAccessor"/> the Data API's own
    /// filter writes to, because that is what a host embedding the dashboard has to do — there is no second
    /// seam for "who is this in-process call acting as".
    /// </remarks>
    /// <param name="world">The running world.</param>
    /// <param name="roleName">The declared role the caller holds.</param>
    /// <returns>The registered <see cref="IAlvoManagement"/>.</returns>
    private static IAlvoManagement Publish(AlvoApiWorld world, string roleName)
    {
        var catalog = world.Services.GetRequiredService<IRoleCatalogProvider>().DeclaredRoles
            ?? RoleCatalog.BuiltInOnly;

        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext
            {
                User = new UserId(Guid.NewGuid()),
                Roles = catalog.Resolve([roleName, Role.Authenticated.Name]),
            },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "in-process",
        };

        return world.Services.GetRequiredService<IAlvoManagement>();
    }

    /// <summary>Publishes nobody, and hands back the same contract implementation.</summary>
    /// <param name="world">The running world.</param>
    /// <returns>The registered <see cref="IAlvoManagement"/>, with no caller published.</returns>
    private static IAlvoManagement Unpublish(AlvoApiWorld world)
    {
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = null;

        return world.Services.GetRequiredService<IAlvoManagement>();
    }

    /// <summary>The running test's cancellation token, as xUnit1051 requires every awaited call to carry.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The descriptor with the developer's own role handed administration and the viewer dropped.</summary>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    private static string Escalated(string descriptorJson) =>
        DescriptorEdits.GrantAdminTo(descriptorJson, role: "dispatcher");
}
