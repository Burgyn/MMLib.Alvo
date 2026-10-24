using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <see cref="ManagedFleet"/>'s project with three handlers registered as a host would: one on <c>vehicles</c> reading
/// <c>plate</c>, one on every entity, one on an application event.
/// </summary>
internal static class ManagedExtensions
{
    internal const string PlateChanged = "vehicle-plate-changed";
    internal const string EveryWrite = "every-write-audited";
    internal const string FleetAlerted = "fleet-alerted";
    internal const string PlateChangedDescription = "Re-registers the vehicle when its plate changes.";

    /// <summary>Starts the world; the boot binds the three handlers strictly, as a code-first host's does.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    internal static Task<AlvoApiWorld> StartAsync(IReadOnlyList<TestApiKey> keys) =>
        AlvoApiWorld.FromDescriptorAsync(
            ManagedFleet.Descriptor,
            keys,
            new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true, ConfigureServicesAfterAlvo: Register));

    private static void Register(IServiceCollection services) => new Builder(services)
        .Entity("vehicles", vehicles => vehicles.OnUpdated(
            PlateChanged,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(plate)",
            configure: options => options.Description = PlateChangedDescription))
        .Entity("*", every => every.OnSaved(EveryWrite, (AlvoSaved<AlvoRecord> _) => Task.CompletedTask))
        .AddEventHandler(FleetAlerted, "fleet.alerted", (AlvoRecord _) => Task.CompletedTask);

    /// <summary>The builder a host holds; the registration verbs read only <see cref="Services"/>.</summary>
    private sealed class Builder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
