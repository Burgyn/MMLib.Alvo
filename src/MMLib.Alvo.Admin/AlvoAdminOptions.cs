namespace MMLib.Alvo.Admin;

/// <summary>
/// What a host may change about the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. The dashboard is one screen set over one contract
/// (<c>IAlvoManagement</c>); everything it shows comes from the descriptor, the schema and
/// <c>capabilities</c>, so there is nothing here to configure that the descriptor does not already
/// own.
/// </para>
/// <para>
/// <b>There is no configurable base path, and that is a decision rather than an omission.</b> A
/// routable Razor component's <c>@page</c> directive is a compile-time constant, so a mount point
/// read from configuration would have to be implemented as a nested pipeline branch — and every
/// link, redirect and <c>NavigationManager</c> call inside the dashboard would then have to learn
/// about a prefix the router does not know. The dashboard therefore lives at
/// <see cref="AlvoAdmin.BasePath"/>, and a deployment that needs it elsewhere moves the whole
/// application with <c>UsePathBase</c>, which is the mechanism ASP.NET Core already has for this
/// and the one the standalone host already exposes as <c>Alvo:PathBase</c>.
/// </para>
/// </remarks>
public sealed class AlvoAdminOptions
{
    /// <summary>Whether the dashboard is mapped at all. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// An embedded host that references this package for its design system alone, or a deployment
    /// that fronts Alvo with its own console, turns it off here rather than by not calling
    /// <c>MapAlvoAdmin</c> — so the switch is configuration, readable in one place beside the rest
    /// of the <c>Alvo:</c> section, rather than a line of code somebody has to find.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}
