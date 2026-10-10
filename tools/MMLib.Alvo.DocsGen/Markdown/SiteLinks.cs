namespace MMLib.Alvo.DocsGen.Markdown;

internal static class SiteLinks
{
    internal const string SiteUrl = "https://alvo.burgyn.online";
    /// <summary>The site's path prefix: astro.config.mjs's <c>base</c> without its trailing slash, so empty for a site served at the root.</summary>
    internal const string BasePath = "";
    internal const string Repository = "https://github.com/Burgyn/MMLib.Alvo";

    internal static string Page(string slug) => $"{BasePath}/{slug.Trim('/')}/";

    internal static string Absolute(string slug) => SiteUrl + Page(slug);

    internal static string RepoBlob(string repoPath) => $"{Repository}/blob/main/{repoPath.TrimStart('/')}";

    internal static string RepoEdit(string repoPath) => $"{Repository}/edit/main/{repoPath.TrimStart('/')}";
}
