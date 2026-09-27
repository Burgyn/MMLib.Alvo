using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity;

/// <summary>
/// Cookie sign-in for the humans who administer a project.
/// </summary>
/// <remarks>
/// <b>Separate from <c>AddAlvoIdentity</c> on purpose.</b> That one registers the store and the
/// context resolver — everything an embedded host needs to resolve <em>its own</em> already
/// authenticated users through Alvo. This one adds an authentication scheme, which is a decision
/// about the whole application: a host that already has cookie authentication of its own must not
/// have a second scheme registered underneath it.
/// </remarks>
public static class AlvoIdentityAuthenticationExtensions
{
    /// <summary>
    /// Adds the cookie scheme, the sign-in manager and <see cref="AlvoSignIn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cookie is <c>HttpOnly</c>, <c>SameSite=Lax</c> and <c>SecurePolicy=SameAsRequest</c>.
    /// <c>Lax</c> rather than <c>Strict</c> because a sign-in that followed a link from anywhere
    /// else would otherwise land on the sign-in page again; <c>SameAsRequest</c> rather than
    /// <c>Always</c> because the zero-configuration first run is <c>http://localhost</c> and a
    /// cookie the browser refuses to store is a sign-in that silently never completes. A
    /// deployment behind TLS gets <c>Secure</c> from the request it is actually serving.
    /// </para>
    /// <para>
    /// The unauthenticated redirect goes to <paramref name="signInPath"/> and the caller supplies
    /// it, because the screen belongs to whoever is rendering one — <c>MMLib.Alvo.Admin</c> in the
    /// standalone image, the host's own page in an embedded one.
    /// </para>
    /// <para>
    /// <b>A session is re-checked, not trusted for its eight hours.</b> The cookie is re-validated
    /// against the store on every request to an endpoint that guards something (not static assets or
    /// the sign-in page), and a Blazor circuit's authentication state every thirty
    /// seconds; the cookie of a disabled or deleted account, or of one whose password was set since the
    /// cookie was minted (its security stamp no longer matches), is rejected and cleared, and its open tab
    /// drops to sign-in. See <c>AlvoSessionValidation</c> and
    /// <c>AlvoIdentityRevalidatingAuthenticationStateProvider</c> for why each exists.
    /// </para>
    /// <para>
    /// <b>The circuit's provider is registered last-wins, and a host decides which one it gets.</b> This
    /// method adds <c>AuthenticationStateProvider</c> with a plain <c>AddScoped</c>, so it replaces a provider
    /// the host registered <em>before</em> calling it, and a provider the host registers <em>after</em> it
    /// replaces this one — and with it the circuit's re-check. A host with a provider of its own should
    /// register it after this call and make it revalidate (deriving from
    /// <c>RevalidatingServerAuthenticationStateProvider</c>); a host that wants this one should register none.
    /// Either way the cookie re-check stays, and every management and data call still re-resolves its caller.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="signInPath">Where an unauthenticated request is sent.</param>
    /// <param name="accessDeniedPath">Where an authenticated but unauthorized request is sent.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAlvoIdentityCookieSignIn(
        this IServiceCollection services, string signInPath, string? accessDeniedPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(signInPath);

        /* SignInManager reads the current HttpContext to write the cookie onto the response, so it
           takes IHttpContextAccessor as a constructor dependency. Nothing else in Alvo needs one —
           the Data API resolves its caller from the request it is handed — so this is registered
           here, beside the one thing that requires it, rather than in AddAlvoIdentity where it
           would be a dependency every embedded host acquired for nothing. */
        services.AddHttpContextAccessor();

        services.AddScoped<
            IUserClaimsPrincipalFactory<AlvoIdentityUser>, AlvoIdentityClaimsFactory>();
        services.AddScoped<SignInManager<AlvoIdentityUser>>();
        services.AddScoped(provider => new AlvoSignIn(
            provider.GetRequiredService<SignInManager<AlvoIdentityUser>>(),
            provider.GetRequiredService<IAlvoBootstrapAdmin>(),
            provider.GetRequiredService<AlvoIdentityDbContext>(),
            provider.GetRequiredService<AlvoTimingParity>()));

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
            {
                cookie.LoginPath = signInPath;
                cookie.LogoutPath = signInPath;
                cookie.AccessDeniedPath = accessDeniedPath ?? signInPath;
                cookie.Cookie.Name = "alvo.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.Events.OnValidatePrincipal = AlvoSessionValidation.ValidateCookieAsync;
            });

        /* The circuit's half of the same re-check. Added, not tried: AddRazorComponents registers
           its plain provider with TryAdd, so this wins whichever of the two a host calls first, and
           the last registration is the one the container hands out. */
        services.AddScoped<AuthenticationStateProvider, AlvoIdentityRevalidatingAuthenticationStateProvider>();

        services.AddAuthorization();

        return services;
    }
}
