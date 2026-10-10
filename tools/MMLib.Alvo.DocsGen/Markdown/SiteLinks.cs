namespace MMLib.Alvo.DocsGen.Markdown;

internal static class SiteLinks
{
    internal const string SiteUrl = "https://burgyn.github.io";
    internal const string BasePath = "/MMLib.Alvo";
    internal const string Repository = "https://github.com/Burgyn/MMLib.Alvo";

    internal static string Page(string slug) => $"{BasePath}/{slug.Trim('/')}/";

    internal static string Absolute(string slug) => SiteUrl + Page(slug);

    internal static string RepoBlob(string repoPath) => $"{Repository}/blob/main/{repoPath.TrimStart('/')}";

    internal static string RepoEdit(string repoPath) => $"{Repository}/edit/main/{repoPath.TrimStart('/')}";
}
