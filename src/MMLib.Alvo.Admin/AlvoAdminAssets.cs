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
/// The design system is CSS and the screens are Razor components, and neither is a type a consumer
/// is meant to call — some sixty component classes are technically <c>public</c> (the Razor SDK
/// gives every one of them that accessibility; see <c>Properties/AssemblyInfo.cs</c>), but
/// <c>[EditorBrowsable(Never)]</c> on the whole namespace keeps them out of a consumer's
/// IntelliSense and out of the contract this package keeps stable. This type, alongside
/// <see cref="AlvoAdminOptions"/>, <see cref="AlvoAdminClaims"/>,
/// <see cref="IAlvoAdminCallerResolver"/> and the two registration/mapping extension methods, is
/// what actually is.
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

    /// <summary>
    /// The three browser concerns the design system owns: the stored theme, the density, and the
    /// keyboard map.
    /// </summary>
    /// <remarks>
    /// A host loads this in <c>&lt;head&gt;</c> rather than at the end of the body: it applies the
    /// stored theme before the first paint, and a theme applied after paint is a flash. It carries
    /// no framework and takes over nothing Blazor renders — it publishes <c>alvo:*</c> events and
    /// lets the component that owns the surface decide what they mean.
    /// </remarks>
    public static string Script { get; } = $"{ContentRoot}/alvo.js";

    /// <summary>
    /// The interop module the components import — the bridge between <see cref="Script"/>'s
    /// keyboard map and a Blazor circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Script"/> deliberately. That one runs before hydration and knows
    /// nothing about Blazor, which is what makes it correct for a theme applied before the first
    /// paint. This one is an ES module, imported once per circuit on the first call that needs it and
    /// released with the circuit.
    /// </para>
    /// <para>
    /// <b>Rooted at the origin, unlike the other two, and it has to be.</b> Those are written into
    /// a <c>&lt;link&gt;</c> and a <c>&lt;script src&gt;</c>, where the document's <c>&lt;base&gt;</c>
    /// resolves a relative path. This one is handed to <c>import()</c>, and a specifier that starts
    /// with neither <c>/</c> nor <c>./</c> is a <em>bare specifier</em> — a package name, which a
    /// browser with no import map cannot resolve. The import rejects, the dashboard logs it at error
    /// and every keyboard shortcut and overlay gesture does nothing — a page that renders but will not
    /// answer ⌘K, which reads as a broken application rather than as a missing file.
    /// </para>
    /// </remarks>
    public static string Module { get; } = $"/{ContentRoot}/admin.js";

    /// <summary>
    /// The mark on its own — a rounded square carrying the prompt glyph.
    /// </summary>
    /// <remarks>
    /// The same file the reference drawing uses (<c>docs/design/f5-admin</c>), copied rather than
    /// referenced, because nothing shipped may depend on a design artifact. A letter in a coloured
    /// box is what the dashboard drew before this, and it was a placeholder that outlived its
    /// placeholder-ness.
    /// </remarks>
    public static string Mark { get; } = $"{ContentRoot}/alvo-mark.svg";

    /// <summary>The mark with the name and the line beneath it, for a screen with room for it.</summary>
    public static string Wordmark { get; } = $"{ContentRoot}/alvo-wordmark.svg";
}
