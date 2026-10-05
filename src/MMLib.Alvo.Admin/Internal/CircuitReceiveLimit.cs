using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Raises the Blazor circuit hub's receive limit to <see cref="ImportLimit.CircuitReceiveBytes"/> — only while the
/// dashboard is enabled, and only ever upwards (#316).
/// </summary>
/// <remarks>
/// <para>
/// <b>The hub is the host's as well.</b> Every server-interactive circuit in the process shares one
/// <c>HubOptions&lt;ComponentHub&gt;</c>, so an embedded host's own Blazor circuits get the raised limit too; and it acts on
/// a <c>/_blazor</c> connection before anyone has signed in. That is why a host that set
/// <see cref="AlvoAdminOptions.Enabled"/> to <see langword="false"/> keeps SignalR's own limit: it does not serve the
/// Import box this limit exists for.
/// </para>
/// <para>
/// <b>Only upwards.</b> A host that allows more, or no limit at all, keeps what it chose. A lower limit the host set
/// before <c>AddAlvoAdmin</c> is raised all the same, because it cannot be told apart from the default; a host that wants
/// the dashboard on and a lower limit sets it after <c>AddAlvoAdmin</c>, and gives up pastes the size of a realistic
/// descriptor.
/// </para>
/// <para>
/// <c>ComponentHub</c> is internal to ASP.NET Core, so the closed options type is read off the registration
/// <c>AddHubOptions</c> itself makes, rather than named.
/// </para>
/// </remarks>
internal static class CircuitReceiveLimit
{
    /// <summary>Registers the raise against the circuit hub that <paramref name="blazor"/> configures.</summary>
    /// <param name="blazor">The server-interactive builder.</param>
    public static void Register(IServerSideBlazorBuilder blazor)
    {
        var services = blazor.Services;
        var before = services.Count;
        blazor.AddHubOptions(_ => { });
        var configure = services.Skip(before).Select(descriptor => descriptor.ServiceType)
            .Last(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IConfigureOptions<>));
        var hub = configure.GetGenericArguments()[0].GetGenericArguments()[0];
        services.AddSingleton(configure, typeof(Raise<>).MakeGenericType(hub));
    }

    /// <summary>The raise itself, for the hub type the framework uses.</summary>
    /// <typeparam name="THub">The circuit hub.</typeparam>
    /// <param name="admin">The dashboard's options, read when the hub's options are first built.</param>
    private sealed class Raise<THub>(IOptions<AlvoAdminOptions> admin) : IConfigureOptions<HubOptions<THub>>
        where THub : Hub
    {
        public void Configure(HubOptions<THub> options)
        {
            if (admin.Value.Enabled
                && options.MaximumReceiveMessageSize is { } current
                && current < ImportLimit.CircuitReceiveBytes)
            {
                options.MaximumReceiveMessageSize = ImportLimit.CircuitReceiveBytes;
            }
        }
    }
}
