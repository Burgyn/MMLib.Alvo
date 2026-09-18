namespace MMLib.Alvo.Admin;

/// <summary>
/// The static web assets this package publishes, by the path a host references them at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are a public contract and not a convention.</b> A Razor Class Library serves its
/// <c>wwwroot</c> under <c>_content/&lt;assembly name&gt;/</c>, and a host that wants the dashboard
/// styled has to write that path into a <c>&lt;link&gt;</c> tag. Spelled by hand it is a string
/// that compiles either way and fails only in the browser; spelled here it moves with the package
/// and the compiler catches a rename.
/// </para>
/// <para>
/// Nothing else on this assembly is public. The design system is CSS, the components are Razor,
/// and neither is a type a consumer calls — so <c>public</c> stops here, which is
/// <c>alvo-architecture-rules</c>' own default rather than an omission.
/// </para>
/// </remarks>
public static class AlvoAdminAssets
{
    /// <summary>The prefix a Razor Class Library's static web assets are served under.</summary>
    private const string ContentRoot = "_content/MMLib.Alvo.Admin";

    /// <summary>
    /// The design system — tokens, both themes, and every component class.
    /// </summary>
    /// <remarks>
    /// This is the one stylesheet. There is no second sheet to load in a particular order, and no
    /// component library beneath it (design deviation D1), so a host links exactly this.
    /// </remarks>
    public static string StyleSheet { get; } = $"{ContentRoot}/alvo.css";
}
