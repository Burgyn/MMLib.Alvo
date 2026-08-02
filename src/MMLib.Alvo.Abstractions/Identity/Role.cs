namespace MMLib.Alvo;

/// <summary>
/// A role a caller holds. A caller holds a <em>set</em> of roles, and CEL exposes them as
/// <c>@user.roles</c> with membership via <c>in</c> — the built-in trio is not one axis
/// (<c>anon</c> / <c>authenticated</c> describe whether the caller is logged in, <c>admin</c>
/// describes privilege), so a single slot cannot answer "any logged-in user may read".
/// </summary>
/// <remarks>
/// Two deliberate properties: <c>default(Role)</c> is <see cref="Anon"/> — the
/// least-privileged value, so an uninitialized field fails safe instead of open — and an
/// application role can only be minted through a <see cref="RoleCatalog"/> built from the
/// descriptor, so a typo is rejected where it enters rather than silently matching no rule.
/// </remarks>
public readonly record struct Role
{
    private const string AnonName = "anon";

    private readonly string? _name;

    private Role(string name) => _name = name;

    /// <summary>The anonymous, least-privileged role; also the value of <c>default(Role)</c>.</summary>
    public static Role Anon { get; } = new(AnonName);

    /// <summary>Every authenticated caller, regardless of privilege.</summary>
    public static Role Authenticated { get; } = new("authenticated");

    /// <summary>The built-in administrative role.</summary>
    public static Role Admin { get; } = new("admin");

    /// <summary>Gets the role name as it appears in the descriptor and in CEL.</summary>
    public string Name => _name ?? AnonName;

    internal static Role Application(string name) => new(name);

    /// <inheritdoc />
    public bool Equals(Role other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;
}
