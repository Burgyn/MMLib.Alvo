namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The callers every tenant-role fact reads: one holding <c>dispatcher</c> as a tenant role, one holding it as a project
/// role, and one holding neither — all authenticated in the Acme tenant.
/// </summary>
internal static class TenantRoleCallers
{
    /// <summary>The declared application role <c>dispatcher</c>, as a one-role set.</summary>
    internal static IReadOnlySet<Role> Dispatcher { get; } = RoleCatalog.Create(["dispatcher"]).Resolve(["dispatcher"]);

    /// <summary>An authenticated Acme caller holding <c>dispatcher</c> as a tenant role only.</summary>
    internal static AlvoContext Person { get; } =
        CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated }, TenantRoles = Dispatcher };

    /// <summary>An authenticated Acme caller with no tenant roles.</summary>
    internal static AlvoContext Plain { get; } = CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated } };

    /// <summary>An authenticated Acme caller holding <c>dispatcher</c> as a project role.</summary>
    internal static AlvoContext ProjectDispatcher { get; } =
        Plain with { Roles = new HashSet<Role>(Dispatcher) { Role.Authenticated } };
}
