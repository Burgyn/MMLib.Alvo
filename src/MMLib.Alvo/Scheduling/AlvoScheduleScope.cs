namespace MMLib.Alvo.Scheduling;

/// <summary>Where one occurrence of a schedule runs.</summary>
public enum AlvoScheduleScope
{
    /// <summary>Once, across the whole project, with no tenant.</summary>
    Global = 0,

    /// <summary>
    /// Once per tenant the host's <see cref="IAlvoTenantCatalog"/> lists — or, with none registered and <c>tenancy.selfService</c>
    /// declared, once per self-service tenant — each run reading only its own tenant's rows.
    /// </summary>
    EachTenant = 1,
}
