using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MudBlazor.Services;

namespace MMLib.Alvo.Admin;

/// <summary>
/// Registers the dashboard.
/// </summary>
public static class AlvoAdminServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Razor components the dashboard is built from, and the server-interactive render
    /// mode they run in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Server-interactive, not WebAssembly, and the reason is the contract.</b> The dashboard
    /// reads and writes through <c>IAlvoManagement</c>, resolved from DI in the same process
    /// (design §1.2, <i>one application service, two transports</i>). A WebAssembly dashboard could
    /// not do that — it would have to reach its own process over HTTP, which is the second
    /// transport, and would then need the whole Management API surface to be reachable from a
    /// browser origin before the first screen rendered.
    /// </para>
    /// <para>
    /// What is registered below — <c>ManagementGateway</c>, <c>DataGateway</c>,
    /// <c>AssistantGateway</c>, <c>WorkingCopyStore</c>, <c>AdminSession</c>, <c>AdminInterop</c> and
    /// <c>SetPasswordRoute</c> — is
    /// internal, and every one of them is a thin adapter over <c>IAlvoManagement</c>, the ports in
    /// <c>MMLib.Alvo.Abstractions</c> or the browser rather than a service of the dashboard's own: a screen that
    /// needed one would be a screen doing work the core should be doing (<c>AdminSession</c> only composes the
    /// others for a screen, <c>AdminInterop</c> is the one path into the dashboard's script, and
    /// <c>SetPasswordRoute</c> reads the host's own routing table). None of these seven is public, and none is meant to be resolved by host code. Beside them it registers the component library
    /// the screens are drawn with: MudBlazor's own services, through <c>AddLibrary</c>.
    /// </para>
    /// <para>
    /// <b>It adds no session re-check of its own.</b> The dashboard's chrome — which screens the router
    /// renders — follows the host's <c>AuthenticationStateProvider</c>, and a signed-in session is re-checked only
    /// if the host's provider does it. <c>MMLib.Alvo.Identity</c>'s cookie sign-in registers one that does; a
    /// host that mints its own sessions owns their revalidation, and without it a disabled operator's open tab
    /// keeps navigating until its next page load. Authority never lingers either way: every management and
    /// data call re-resolves its caller through <see cref="IAlvoAdminCallerResolver"/> and is refused at once.
    /// </para>
    /// <para>
    /// <b>The set-password post is the host's to map.</b> Access hands a credential token over as a link to
    /// <see cref="AlvoAdmin.SetPasswordPath"/>, whose form posts to <see cref="AlvoAdmin.SetPasswordEndpoint"/>.
    /// Redeeming it needs the identity package, which this one does not reference, so <see cref="AlvoAdminEndpointRouteBuilderExtensions.MapAlvoAdmin"/> does
    /// not map it: <c>MMLib.Alvo.Host</c> does, and an embedded host that registers <c>IAlvoUserAdministration</c>
    /// maps its own <c>POST</c> there over <c>AlvoSignIn.SetPasswordAsync</c> (the host's
    /// <c>AlvoAdminSetPassword</c> shows the order its checks must run in). Until it does, Access finds no such route
    /// in the host's endpoints and hands over the bare token instead, with one sentence saying the host has no
    /// set-password page — never a link to a form that posts nowhere.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Configures the dashboard.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAlvoAdmin(
        this IServiceCollection services, Action<AlvoAdminOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AlvoAdminOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        /* SignalR's default 32 KB receive limit is under a realistic descriptor, and a box over it closed the circuit
           without a word (#316). ImportLimit says why 2 MiB. Raised only: a host that already allows more, or set
           no limit at all, keeps what it chose. */
        services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(hub =>
        {
            if (hub.MaximumReceiveMessageSize is { } current && current < ImportLimit.CircuitReceiveBytes)
            {
                hub.MaximumReceiveMessageSize = ImportLimit.CircuitReceiveBytes;
            }
        });
        services.AddCascadingAuthenticationState();

        /* AuthorizeRouteView asks IAuthorizationService on every in-circuit navigation, and a
           circuit has no endpoint to carry the answer. Registering it here rather than relying on
           the host is the difference between the router checking and the router silently not
           checking: the call is idempotent, so a host that already called AddAuthorization keeps
           its own policies. */
        services.AddAuthorizationCore();
        AddLibrary(services);

        /* Scoped: in Blazor Server a scope is a circuit, so one operator's session holds one
           gateway and its cache never crosses to another's. Its remarks argue why that cache is a
           correctness property rather than a speed one. */
        /* Through a factory: IAlvoUserAdministration is registered only by a deployment that has
           a membership store, and a nullable constructor parameter is not an optional dependency
           to the container. The same shape the core already uses for an optional data port. */
        /* And it follows the circuit's navigation from the moment it exists, which is what keeps its cache
           from outliving an apply made in another circuit — ManagementGateway.FollowNavigation says how. The
           price: the gateway can be resolved only where the NavigationManager is initialised, inside a
           component's render. Resolving it from an endpoint, a middleware or a CircuitHandler throws. */
        services.TryAddScoped(provider =>
        {
            var gateway = new ManagementGateway(
                provider.GetRequiredService<IAlvoManagement>(),
                provider.GetService<IAlvoUserAdministration>(),
                provider.GetRequiredService<IAlvoAdminCallerResolver>(),
                provider.GetRequiredService<AuthenticationStateProvider>(),
                provider.GetRequiredService<IAlvoContextAccessor>());
            gateway.FollowNavigation(provider.GetRequiredService<NavigationManager>());
            return gateway;
        });
        services.TryAddScoped<DataGateway>();

        /* Both of the assistant gateway's own dependencies are optional, and neither absence is an error: a
           deployment that installed no agent package has no IAlvoAssistant, and one whose secrets are
           deployed rather than clicked has no writable ISecretStore. The same factory shape the management
           gateway already uses for the membership store, for the same reason — a nullable constructor
           parameter is not an optional dependency to the container. */
        services.TryAddScoped(provider => new AssistantGateway(
            provider.GetService<MMLib.Alvo.Ai.IAlvoAssistant>(),
            provider.GetService<MMLib.Alvo.Secrets.ISecretStore>(),
            provider.GetRequiredService<ManagementGateway>()));

        /* One working copy per circuit: there is one descriptor and one apply, so three screens
           editing three things are still editing one document (§4.5). */
        /* A singleton store, not a scoped copy: a Blazor Server scope is a circuit and a circuit
           ends on a browser reload, so a copy held there would discard unapplied edits on F5. One
           descriptor and one apply means one copy per operator (§4.5) — not one per screen, and
           never one shared between operators. */
        services.TryAddSingleton(provider => new WorkingCopyStore(
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        /* Scoped like the gateways it composes: one circuit's caller and working copy. */
        services.TryAddScoped<AdminSession>();

        /* Scoped for the same reason: one import of the dashboard's script per circuit. */
        services.TryAddScoped<AdminInterop>();

        /* Whether the host maps the set-password post, read from its endpoints when a token is handed over. */
        services.TryAddSingleton<SetPasswordRoute>();

        return services;
    }

    /// <summary>
    /// The component library's services, as the library ships them.
    /// </summary>
    /// <remarks>
    /// Registered here so an embedded host never learns the library exists (study §1.3). <b>Nothing process-wide is
    /// configured</b>: the library's options are one object per container, so a snackbar position or a breakpoint
    /// set here would move an embedding host's own MudBlazor UI too — the argument spec §3.8 makes against shared
    /// input defaults (final review I6). The dashboard says what it needs where only it is reached: each
    /// confirmation carries its own duration, close button and duplicate rule and keeps at most two on screen
    /// (<c>AdminSnackbar</c>), and its snackbar provider is placed bottom-left by a class of its own in
    /// <c>alvo.css</c>. The drawer follows Alvo's 720 px phone width through <c>AdminLayout</c>'s own viewport
    /// subscription, so no library breakpoint needs to move. The trade-off, recorded: a host that sets the library's
    /// options still reaches the dashboard's snackbars through the two the provider reads for itself, the stacking
    /// order and the most shown at once.
    /// </remarks>
    private static void AddLibrary(IServiceCollection services) => services.AddMudServices();
}
