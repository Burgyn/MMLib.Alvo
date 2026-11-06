namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The tenants this host serves, as the host itself knows them — Alvo keeps no tenant list of its own. A schedule that runs
/// once per tenant asks it for the tenants to run in.
/// </summary>
/// <remarks>Register one in the host's services to use <c>AlvoScheduleScope.EachTenant</c>; without one such a schedule refuses the start.</remarks>
public interface IAlvoTenantCatalog
{
    /// <summary>Lists the tenants a per-tenant run is made for, each once.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tenants, in the order their runs are created.</returns>
    Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default);
}
