using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
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
internal sealed class AlvoManagementService(
    IOptions<AlvoOptions> alvo,
    IOptions<AlvoManagementOptions> management,
    IOptions<AlvoSchemaOptions> schema,
    AlvoBootState boot,
    ISchemaRegistry schemaRegistry,
    IPolicyEngine policies,
    IRoleCatalogProvider roles,
    IAlvoData? data,
    IDescriptorVersionStore? versions) : IAlvoManagement
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
    /// The boot is the one authority on which projects exist here, so this is the only check any member
    /// needs — and it is what keeps an unknown name a 404 rather than another project's answer, on a surface
    /// whose collaborators (<c>ISchemaRegistry</c>, <c>IPolicyEngine</c>) still carry no project parameter at
    /// all.
    /// </remarks>
    /// <param name="project">The project name the caller asked for.</param>
    /// <exception cref="ManagementProjectNotFoundException">This instance serves no such project.</exception>
    private void EnsureServed(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
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
