using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// <c>POST {prefix}/token</c> (RFC 6749 §3.2): reads the form, refusing a malformed one before any grant sees it, and
/// hands it to the grant its <c>grant_type</c> names.
/// </summary>
/// <param name="exchange">The token-exchange grant (RFC 8693).</param>
/// <param name="refresh">The refresh grant (RFC 6749 §6).</param>
internal sealed class TokenEndpoint(ExchangeGrant exchange, RefreshGrant refresh)
{
    private const string GrantTypeParameter = "grant_type";

    /// <summary>Answers one token request.</summary>
    /// <param name="http">The request's context.</param>
    internal async Task<IResult> HandleAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        if (await TokenForm.ReadAsync(http.Request, ct).ConfigureAwait(false) is not { } form)
        {
            return TokenProblems.MalformedForm();
        }

        return form[GrantTypeParameter] switch
        {
            null => TokenProblems.MissingGrantType(),
            ExchangeGrant.GrantType => await exchange.ExchangeAsync(form, http.Request, ct).ConfigureAwait(false),
            RefreshGrant.GrantType => await refresh.RefreshAsync(form, http.Request, ct).ConfigureAwait(false),
            _ => TokenProblems.UnsupportedGrantType(),
        };
    }
}
