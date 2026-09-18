using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
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
/// <param name="data">The registered data port, or <see langword="null"/> when the host registered none.</param>
/// <param name="versions">
/// The descriptor history, or <see langword="null"/> when no provider registered one.
/// </param>
internal sealed class AlvoManagementService(
    IOptions<AlvoOptions> alvo,
    IOptions<AlvoManagementOptions> management,
    IOptions<AlvoSchemaOptions> schema,
    AlvoBootState boot,
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
