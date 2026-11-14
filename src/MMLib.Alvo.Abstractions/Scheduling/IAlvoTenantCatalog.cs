namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The tenants this host serves, as the host itself knows them. A schedule that runs once per tenant asks it for the tenants
/// to run in.
/// </summary>
/// <remarks>
/// Register one in the host's services to use <c>AlvoScheduleScope.EachTenant</c>; without one such a schedule refuses the
/// start — unless the descriptor declares <c>tenancy.selfService</c>, where Alvo lists the self-service tenants of its own
/// registry instead. That default lists <b>self-service tenants only</b>, and of those the ones the descriptor honours (a
/// personal tenant only while <c>personalTenant</c> is declared): a host that also serves tenants created by API-key
/// workflows registers its own catalog, which always wins. Under an open sign-up with <c>personalTenant</c> the default
/// grows by one tenant per account, and every per-tenant run with it; register a catalog of your own to bound that.
/// </remarks>
public interface IAlvoTenantCatalog
{
    /// <summary>Lists the tenants a per-tenant run is made for, each once.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tenants, in the order their runs are created.</returns>
    Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default);
}
