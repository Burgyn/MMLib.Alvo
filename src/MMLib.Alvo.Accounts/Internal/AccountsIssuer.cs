using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The issuer and audience every access token is minted and validated with (spec §5.4).</summary>
internal static class AccountsIssuer
{
    /// <summary>
    /// The configured <see cref="AlvoAccountsOptions.Issuer"/>; in Development only, when none is configured, the URL the
    /// request reached the sign-in routes at.
    /// </summary>
    /// <remarks>
    /// Outside Development the request is never consulted: its <c>Host</c> header is the caller's to choose, and an issuer
    /// a caller chooses is no issuer. Startup refuses such a host without an issuer, so reaching the throw is a defect.
    /// </remarks>
    /// <param name="request">The request being answered.</param>
    /// <param name="options">The accounts options.</param>
    internal static string IssuerFor(HttpRequest request, AlvoAccountsOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrEmpty(options.Issuer))
        {
            return options.Issuer;
        }

        return IsDevelopment(request)
            ? $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}{options.Prefix}"
            : throw new InvalidOperationException(
                "Alvo:Accounts:Issuer is not set outside Development; startup validation should have refused this host.");
    }

    /// <summary>The configured <see cref="AlvoAccountsOptions.Audience"/>, or else the applied descriptor's name.</summary>
    /// <param name="options">The accounts options.</param>
    /// <param name="policy">The applied descriptor's sign-in policy.</param>
    internal static string AudienceFor(AlvoAccountsOptions options, SignInPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        return string.IsNullOrEmpty(options.Audience) ? policy.ProjectName : options.Audience;
    }

    private static bool IsDevelopment(HttpRequest request)
        => request.HttpContext.RequestServices?.GetService<IHostEnvironment>()?.IsDevelopment() == true;
}
