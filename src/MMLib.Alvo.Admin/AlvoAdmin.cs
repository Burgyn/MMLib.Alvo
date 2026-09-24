namespace MMLib.Alvo.Admin;

/// <summary>
/// The paths and names the dashboard publishes, for a host that has to spell one of them.
/// </summary>
/// <remarks>
/// <para>
/// The same argument <see cref="AlvoAdminAssets"/> makes: spelled by hand these are strings that
/// compile either way and fail only in the browser; spelled here they move with the package.
/// </para>
/// <para>
/// <b>This type, <see cref="AlvoAdminAssets"/>, <see cref="AlvoAdminOptions"/>,
/// <see cref="AlvoAdminClaims"/>, <see cref="IAlvoAdminCallerResolver"/> and the two
/// <c>Add</c>/<c>Map</c> extension methods are the package's public contract.</b> Everything under
/// <c>Components.*</c> is implementation — the Razor SDK emits every component class as
/// <c>public</c> because there is no per-component accessibility to set, not because a host is
/// meant to construct or extend one — and it may change in any minor version. See
/// <c>Properties/AssemblyInfo.cs</c> and docs/architecture/admin-dashboard-review.md (F-10) for the
/// full reasoning.
/// </para>
/// </remarks>
public static class AlvoAdmin
{
    /// <summary>The configuration section the dashboard's options bind from.</summary>
    public const string ConfigurationSection = "Alvo:Admin:Dashboard";

    /// <summary>Where the dashboard is served.</summary>
    /// <remarks>See <see cref="AlvoAdminOptions"/> for why this is a constant and not an option.</remarks>
    public const string BasePath = "/admin";

    /// <summary>The sign-in screen.</summary>
    public const string SignInPath = $"{BasePath}/sign-in";

    /// <summary>
    /// Where the sign-in form posts, and where signing out posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A form post, not a circuit call, and the reason is a hard one.</b> Signing in writes an
    /// authentication cookie, and a cookie is written onto a response — a server-interactive
    /// component runs over a WebSocket, long after the response that carried the page has
    /// completed, so <c>SignInAsync</c> inside a circuit throws. Every Blazor application solves
    /// this the same way: a statically rendered form that posts to an ordinary endpoint.
    /// </para>
    /// <para>
    /// The endpoint itself is not in this package. Signing in needs ASP.NET Core Identity's
    /// <c>SignInManager</c>, and this package holds no reference to
    /// <c>MMLib.Alvo.Identity</c> — the host owns both, so the host maps it. The path is here so
    /// the form and the endpoint cannot drift.
    /// </para>
    /// </remarks>
    public const string SignInEndpoint = $"{BasePath}/sign-in/submit";

    /// <summary>Where signing out posts.</summary>
    /// <remarks>A post, not a link: a sign-out reachable by <c>GET</c> can be triggered by any
    /// page that embeds an image pointing at it.</remarks>
    public const string SignOutEndpoint = $"{BasePath}/sign-out";
}
