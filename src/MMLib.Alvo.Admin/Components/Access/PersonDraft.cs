namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>The roles and the tenant being edited for one person, against what the store says they are.</summary>
internal sealed class PersonDraft(IReadOnlyCollection<string> roles, string? tenant)
{
    private readonly HashSet<string> _roles = new(roles, StringComparer.Ordinal);
    private readonly string _tenant = tenant?.Trim() ?? string.Empty;

    /// <summary>The roles as the editor has them.</summary>
    public IReadOnlyList<string> Roles { get; private set; } = [.. roles];

    /// <summary>The tenant box's text, trimmed.</summary>
    public string Tenant { get; private set; } = tenant?.Trim() ?? string.Empty;

    /// <summary>Whether the role set differs, order aside.</summary>
    public bool RolesChanged => !_roles.SetEquals(Roles);

    /// <summary>Whether the tenant differs, surrounding space aside.</summary>
    public bool TenantChanged => !string.Equals(_tenant, Tenant, StringComparison.Ordinal);

    /// <summary>Whether closing would lose anything.</summary>
    public bool Dirty => RolesChanged || TenantChanged;

    /// <summary>The roles the chips now say.</summary>
    public void SetRoles(IReadOnlyList<string> roles) => Roles = roles;

    /// <summary>What the tenant box now says.</summary>
    public void SetTenant(string text) => Tenant = text.Trim();
}
