using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MudBlazor;
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
    /// <c>AssistantGateway</c>, <c>WorkingCopyStore</c>, <c>AdminSession</c> and <c>AdminInterop</c> — is
    /// internal, and every one of them is a thin adapter over <c>IAlvoManagement</c>, the ports in
    /// <c>MMLib.Alvo.Abstractions</c> or the browser rather than a service of the dashboard's own: a screen that
    /// needed one would be a screen doing work the core should be doing (<c>AdminSession</c> only composes the
    /// others for a screen, and <c>AdminInterop</c> is the one path into the dashboard's script). None of these
    /// six is public, and none is meant to be resolved by host code. Beside them it registers the component library
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

        services.AddRazorComponents().AddInteractiveServerComponents();
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

        return services;
    }

    /// <summary>
    /// The component library's services, configured for the dashboard's feedback rules.
    /// </summary>
    /// <remarks>
    /// Registered here so an embedded host never learns the library exists (study §1.3). The snackbar settings
    /// are spec §3.3: bottom-left, where no editor's footer is (an editor is a right-hand sheet whose actions sit
    /// bottom-right), a few seconds, at most two, and never a duplicate. The breakpoint is Alvo's
    /// one phone width (720 px, pinned by StylesheetHygieneTests), so the responsive drawer turns temporary where
    /// the bottom bar appears rather than at Mud's own 600.
    /// </remarks>
    private static void AddLibrary(IServiceCollection services)
        => services.AddMudServices(library =>
        {
            library.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomLeft;
            library.SnackbarConfiguration.VisibleStateDuration = 5000;
            library.SnackbarConfiguration.MaxDisplayedSnackbars = 2;
            library.SnackbarConfiguration.PreventDuplicates = true;
            library.SnackbarConfiguration.ShowCloseIcon = true;
            library.ResizeOptions.BreakpointDefinitions = new Dictionary<Breakpoint, int>
            {
                [Breakpoint.Xs] = 0,
                [Breakpoint.Sm] = 720,
                [Breakpoint.Md] = 960,
                [Breakpoint.Lg] = 1280,
                [Breakpoint.Xl] = 1920,
                [Breakpoint.Xxl] = 2560,
            };
        });
}
