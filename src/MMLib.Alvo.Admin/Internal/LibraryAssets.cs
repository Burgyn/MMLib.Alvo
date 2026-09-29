namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The component library's two browser assets, by the path the dashboard's document references them at.
/// </summary>
/// <remarks>
/// Internal, unlike <see cref="AlvoAdminAssets"/>: <c>AdminApp.razor</c> is this package's own document, so no host
/// writes these tags, and a public property would promise a file whose name follows MudBlazor's versioning.
/// </remarks>
internal static class LibraryAssets
{
    /// <summary>The layered import of MudBlazor's stylesheet (<c>wwwroot/alvo-mud.css</c>), linked before alvo.css.</summary>
    public static string StyleSheet { get; } = "_content/MMLib.Alvo.Admin/alvo-mud.css";

    /// <summary>
    /// MudBlazor's script. It ships no JS initializer, so the tag is what loads it; without it the theme provider
    /// logs a missing-script error on every page (study §1.3).
    /// </summary>
    public static string Script { get; } = "_content/MudBlazor/MudBlazor.min.js";
}
