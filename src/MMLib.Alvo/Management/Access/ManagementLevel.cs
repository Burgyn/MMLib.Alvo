namespace MMLib.Alvo.Management;

/// <summary>
/// What a caller may do with the Management API, as the descriptor's <c>access</c> block resolved it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbered so that "the highest match wins" is an ordinary comparison.</b> The three
/// <em>predicates</em> are independent — a descriptor may name three disjoint role sets — but what a
/// resolved level may <em>do</em> is ordered, because <c>admin</c> is defined by the schema as
/// "everything, plus settings".
/// </para>
/// <para>
/// <b><see cref="None"/> is zero</b>, so an unset or mis-bound value lands on "no access" — the same
/// property <c>default(Role)</c> has for <c>anon</c>.
/// </para>
/// <para>
/// <b>Management levels govern the Management API only.</b> Data access is governed by the descriptor's
/// <c>entities.*.rules</c>, through the ordinary Data API, identically for the dashboard and for
/// everyone else. No second authorization system for data exists.
/// </para>
/// </remarks>
internal enum ManagementLevel
{
    /// <summary>No management access at all.</summary>
    None = 0,

    /// <summary>May read this project's data and configuration, and simulate a policy.</summary>
    Viewer = 1,

    /// <summary>May additionally edit this project's schema, rules and automation.</summary>
    Developer = 2,

    /// <summary>May do everything, including the settings surface.</summary>
    Admin = 3,
}
