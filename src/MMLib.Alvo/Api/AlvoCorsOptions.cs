namespace MMLib.Alvo.Api;

/// <summary>
/// The browser origins Alvo's own routes — the Data API and the sign-in routes — answer CORS for, bound from
/// <c>Alvo:Api:Cors</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No origins, no CORS headers</b> — the behaviour of a host that never configured this, and the right one for a
/// backend no browser on another origin calls. A host that does configure origins needs no <c>UseCors()</c> step: Alvo
/// applies the policy on its own routes, so an embedded host gets it too.
/// </para>
/// <para>
/// <b>Credentials are never allowed, and <c>*</c> is refused at startup.</b> Alvo's callers present a credential in a
/// header (an API key or <c>Authorization: Bearer</c>), never in a cookie, so a browser never needs to send cookies
/// here; naming each origin keeps another site's script from reading answers it was never meant to.
/// </para>
/// </remarks>
public sealed class AlvoCorsOptions
{
    /// <summary>Gets the allowed origins, e.g. <c>https://app.example.com</c>.</summary>
    /// <remarks>
    /// Each an absolute <c>http</c> or <c>https</c> URI with no path (other than <c>/</c>), query or fragment, compared
    /// as its scheme, host and port. Validated at startup.
    /// </remarks>
    public List<string> Origins { get; } = [];
}
