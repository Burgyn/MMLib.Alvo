using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Identity;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// The two endpoints the dashboard's sign-in screen posts to.
/// </summary>
/// <remarks>
/// <para>
/// <b>They live in the host rather than in the dashboard, and that is the package boundary doing
/// its job.</b> Signing in needs ASP.NET Core Identity; <c>MMLib.Alvo.Admin</c> references neither
/// Identity nor the core (design §1.2). The host is the one project that holds both, so it is the
/// one project that can join them — and it is <c>IsPackable=false</c>, so nothing about this
/// reaches a NuGet consumer.
/// </para>
/// <para>
/// <b>Both are <c>POST</c>, and both validate an antiforgery token.</b> A sign-out reachable by
/// <c>GET</c> is triggered by any page anywhere that embeds an image pointing at it. A sign-in
/// without a token is <i>login CSRF</i>: an attacker posts their own credentials from the victim's
/// browser, and the victim then works inside the attacker's account without noticing. The
/// validation is explicit rather than inherited from the middleware, because these handlers read
/// the form themselves — ASP.NET Core validates automatically only for an endpoint that binds
/// <c>[FromForm]</c>, so an implicit version of this would be a guard that quietly never ran.
/// </para>
/// </remarks>
internal static class AlvoAdminSignIn
{
    /// <summary>
    /// Maps the sign-in and sign-out endpoints, unless the dashboard is turned off.
    /// </summary>
    /// <remarks>
    /// <b>It honours the same switch <c>MapAlvoAdmin</c> does, and the two must not drift.</b>
    /// These endpoints exist to serve one screen; a deployment that set
    /// <see cref="AlvoAdminOptions.Enabled"/> to <see langword="false"/> asked for that screen not
    /// to be there, and a password endpoint still answering behind a dashboard nobody can reach is
    /// surface with no purpose — lockout-protected, but reachable, and reachable is the part that
    /// matters to whoever turned the dashboard off.
    /// </remarks>
    /// <param name="app">The route builder to map into.</param>
    public static void MapAlvoAdminSignIn(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<AlvoAdminOptions>>().Value.Enabled)
        {
            return;
        }

        app.MapPost(AlvoAdmin.SignInEndpoint, SignInAsync).ExcludeFromDescription();
        app.MapPost(AlvoAdmin.SignOutEndpoint, SignOutAsync).ExcludeFromDescription();
    }

    /// <summary>
    /// Signs a person in from the posted form and sends them where they were going.
    /// </summary>
    /// <remarks>
    /// The failure answer is a redirect back to the screen with <c>failed=true</c>, not a rendered
    /// error page: the credentials were posted, so re-rendering the same URL would leave them in
    /// the browser's resubmission prompt.
    /// </remarks>
    private static async Task<IResult> SignInAsync(
        HttpContext http, AlvoSignIn signIn, IAntiforgery antiforgery)
    {
        if (!await ValidAsync(http, antiforgery).ConfigureAwait(false))
        {
            return Results.Redirect(AlvoAdmin.SignInPath);
        }

        var form = await http.Request.ReadFormAsync().ConfigureAwait(false);
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = Local(form["returnUrl"].ToString());

        if (email.Length == 0 || password.Length == 0
            || !await signIn.PasswordSignInAsync(email, password).ConfigureAwait(false))
        {
            return Results.Redirect(
                $"{AlvoAdmin.SignInPath}?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        return Results.Redirect(returnUrl);
    }

    /// <summary>Clears the cookie and returns to the sign-in screen.</summary>
    private static async Task<IResult> SignOutAsync(
        HttpContext http, AlvoSignIn signIn, IAntiforgery antiforgery)
    {
        if (await ValidAsync(http, antiforgery).ConfigureAwait(false))
        {
            await signIn.SignOutAsync().ConfigureAwait(false);
        }

        return Results.Redirect(AlvoAdmin.SignInPath);
    }

    /// <summary>Validates the antiforgery token on a posted form.</summary>
    /// <remarks>
    /// A failure is answered with the sign-in screen rather than a <c>400</c>: the ordinary cause
    /// is a form left open until its token expired, and a person who reads "Bad Request" has no
    /// idea that submitting again would work.
    /// </remarks>
    private static async Task<bool> ValidAsync(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reduces a return URL to something inside this application, or to the dashboard's root.
    /// </summary>
    /// <remarks>
    /// <b>An open redirect is how a sign-in page becomes a phishing page:</b> a link to the real
    /// host with <c>returnUrl</c> pointing elsewhere signs somebody in and then hands them to the
    /// attacker's page, with the address bar having shown the right origin the whole way. Anything
    /// that is not a single-slash-rooted local path is discarded rather than repaired —
    /// <c>//evil.example</c> and <c>/\evil.example</c> are both protocol-relative URLs that a naive
    /// "starts with /" check admits.
    /// </remarks>
    /// <param name="candidate">The posted return URL.</param>
    /// <returns>A local path, always.</returns>
    internal static string Local(string? candidate)
        => candidate is { Length: > 1 }
            && candidate[0] == '/'
            && candidate[1] != '/'
            && candidate[1] != '\\'
                ? candidate
                : AlvoAdmin.BasePath;
}
