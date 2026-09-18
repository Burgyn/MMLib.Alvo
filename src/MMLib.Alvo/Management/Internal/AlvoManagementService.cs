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
/// <param name="data">The registered data port, or <see langword="null"/> when the host registered none.</param>
internal sealed class AlvoManagementService(
    IOptions<AlvoOptions> alvo,
    IOptions<AlvoManagementOptions> management,
    IOptions<AlvoSchemaOptions> schema,
    IAlvoData? data) : IAlvoManagement
{
    /// <summary>What <see cref="ManagementInfo.DataProvider"/> reports when no driver is registered.</summary>
    private const string NoDriverRegistered = "none";

    /// <inheritdoc/>
    public Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ManagementInfo(Version, Mode, DataProvider, StartupMode));

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
