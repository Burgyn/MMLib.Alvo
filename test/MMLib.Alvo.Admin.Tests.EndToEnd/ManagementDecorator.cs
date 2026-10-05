using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The shipped management port, forwarded member by member, so a derived world overrides only the one or two it arms.
/// </summary>
/// <param name="inner">The port the host registered.</param>
public abstract class ManagementDecorator(IAlvoManagement inner) : IAlvoManagement
{
    /// <summary>Replaces the host's management port with <paramref name="decorate"/> of it, at the same lifetime.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="decorate">Wraps the shipped port.</param>
    public static void Around(IServiceCollection services, Func<IAlvoManagement, IAlvoManagement> decorate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);
        var shipped = services.Last(entry => entry.ServiceType == typeof(IAlvoManagement) && !entry.IsKeyedService);
        services.Remove(shipped);
        services.Add(new ServiceDescriptor(
            typeof(IAlvoManagement), provider => decorate(Build(shipped, provider)), shipped.Lifetime));
    }

    /// <inheritdoc/>
    public virtual Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default)
        => inner.ListProjectsAsync(ct);

    /// <inheritdoc/>
    public virtual Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default)
        => inner.GetDescriptorAsync(project, ct);

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(string project, CancellationToken ct = default)
        => inner.ListRevisionsAsync(project, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementRevisionDetail> GetRevisionAsync(string project, int revision, CancellationToken ct = default)
        => inner.GetRevisionAsync(project, revision, ct);

    /// <inheritdoc/>
    public virtual Task<SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default)
        => inner.GetSchemaAsync(project, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default)
        => inner.GetCapabilitiesAsync(project, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementCelFunctions> GetCelFunctionsAsync(string project, CancellationToken ct = default)
        => inner.GetCelFunctionsAsync(project, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
        => inner.SimulatePolicyAsync(project, simulation, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementExpressionVerdict> CheckExpressionAsync(
        string project, ManagementExpressionCheck request, CancellationToken ct = default)
        => inner.CheckExpressionAsync(project, request, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default)
        => inner.ApplyDescriptorAsync(project, request, ct);

    /// <inheritdoc/>
    public virtual Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)
        => inner.RollbackAsync(project, targetRevision, request, ct);

    /// <inheritdoc/>
    public virtual Task SetAiConnectionAsync(StoredAiConnection connection, CancellationToken ct = default)
        => inner.SetAiConnectionAsync(connection, ct);

    private static IAlvoManagement Build(ServiceDescriptor shipped, IServiceProvider provider)
        => (IAlvoManagement)(shipped.ImplementationFactory?.Invoke(provider)
            ?? shipped.ImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, shipped.ImplementationType!));
}
