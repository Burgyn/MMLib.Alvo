namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>
/// An endpoint URL as a list may show it: its userinfo, the credentials a URL can carry before its host, masked
/// (final review M7).
/// </summary>
/// <remarks>
/// <para>
/// Apply accepts <c>https://user:pass@example.com/h</c>, and §8 already keeps a URL out of every snackbar, log line and
/// error sentence, because a URL can be its own bearer secret. The Integrations list is where it is still drawn, so it is
/// drawn as <c>https://***@example.com/h</c>: the host and the path, which say where the rows go, and never the
/// credentials.
/// </para>
/// <para>
/// Read off the text as written, not through <see cref="Uri"/>: a URL apply would refuse is still listed, and still must
/// not show what it carries. The authority runs from <c>://</c> to the first <c>/</c>, <c>?</c> or <c>#</c>, and its
/// userinfo is everything before its last <c>@</c> (RFC 3986 §3.2). The edit box keeps the real value: it is the value
/// the operator edits, and the sheet that holds it is the one place the URL is meant to be read.
/// </para>
/// </remarks>
internal static class UrlCredentials
{
    private const string SchemeEnd = "://";
    private const string Mask = "***";

    /// <summary><paramref name="url"/> with its userinfo, if any, replaced by <c>***</c>; otherwise unchanged.</summary>
    /// <param name="url">The URL as declared.</param>
    public static string Masked(string url)
    {
        var authority = url.IndexOf(SchemeEnd, StringComparison.Ordinal);
        if (authority < 0)
        {
            return url;
        }

        authority += SchemeEnd.Length;
        var end = url.IndexOfAny(['/', '?', '#'], authority);
        var host = url.LastIndexOf('@', (end < 0 ? url.Length : end) - 1, (end < 0 ? url.Length : end) - authority);
        return host < 0 ? url : string.Concat(url.AsSpan(0, authority), Mask, url.AsSpan(host));
    }
}
