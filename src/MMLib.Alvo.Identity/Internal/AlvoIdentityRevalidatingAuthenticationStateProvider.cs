using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The Blazor circuit's authentication state, re-checked against the store on an interval so an open
/// tab drops to sign-in once its operator is disabled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cookie check is not enough.</b> A Blazor Server circuit is one long-lived connection:
/// after the first page load, navigating between screens issues no HTTP request, so the cookie's
/// <c>OnValidatePrincipal</c> never runs again and <c>AuthorizeRouteView</c> keeps re-asking a state
/// captured when the circuit opened. This is ASP.NET Core's own remedy — the same base class the
/// Identity template's provider derives from — asking <see cref="AlvoSessionValidation"/> instead of
/// comparing security stamps, because a disable in this package moves the lockout, not the stamp.
/// </para>
/// <para>
/// <b>Thirty seconds.</b> Short enough that a disabled operator's shell stops navigating within a
/// moment of the disable; long enough that an open tab costs one indexed read per half minute. It
/// governs only the shell: every management and data call re-resolves its caller on its own and is
/// refused at once, so the interval is how long the chrome lingers, never how long authority does.
/// </para>
/// <para>
/// <b>A fresh scope per check.</b> The circuit's own scope lives as long as the tab, and a store
/// resolved from it would share the circuit's <c>DbContext</c>; a scope per check is the same unit-of-work
/// rule the administration decorator follows, and holds for a host store that caches per scope.
/// </para>
/// <para>
/// Registered by <c>AddAlvoIdentityCookieSignIn</c> rather than by the dashboard, because this package
/// is the one that knows what the cookie's subject claim means; the dashboard references neither the
/// store's implementation nor its claims. In a host with no Blazor components it is never resolved.
/// </para>
/// </remarks>
/// <param name="loggers">The base class's logger source.</param>
/// <param name="scopes">Creates the scope each check reads the store from.</param>
internal sealed class AlvoIdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggers,
    IServiceScopeFactory scopes) : RevalidatingServerAuthenticationStateProvider(loggers)
{
    /// <summary>How often an open circuit's operator is re-checked.</summary>
    internal static TimeSpan Interval { get; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    protected override TimeSpan RevalidationInterval => Interval;

    /// <summary>Whether the circuit's operator still holds a session, read from a scope of its own.</summary>
    /// <param name="state">The circuit's current authentication state.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the circuit may keep its state.</returns>
    internal async Task<bool> StillStandsAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        return await AlvoSessionValidation.StillStandsAsync(state.User, users, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
        => StillStandsAsync(authenticationState, cancellationToken);
}
