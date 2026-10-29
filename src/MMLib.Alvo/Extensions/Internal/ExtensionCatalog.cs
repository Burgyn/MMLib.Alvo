using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// The host's registrations as metadata — what the Management API, the dashboard and the assistant read. Registrations,
/// not bindings: the apply guard keeps the descriptor from parting from them, so a registered handler is a bound one.
/// A retired id is no extension and is not listed.
/// </summary>
internal sealed class ExtensionCatalog(ExtensionRegistry registry, IOptions<AlvoExtensionOptions> options)
{
    private const string HonouredPrefix = "extensions.";

    private readonly Lazy<IReadOnlyList<ManagementExtension>> _items =
        new(() => Describe(registry, options.Value.ExposeTypeNames));

    /// <summary>Every extension, by <c>Order</c> and then registration order.</summary>
    internal IReadOnlyList<ManagementExtension> Items => _items.Value;

    /// <summary>The kinds this host registers, as the capability report lists them after its honoured blocks.</summary>
    internal IReadOnlyList<string> HonouredKinds =>
        [.. Items.Select(item => HonouredPrefix + WireName(item.Kind)).Distinct(StringComparer.Ordinal)];

    /// <summary>The catalog envelope the Management API answers with.</summary>
    internal ManagementExtensionCatalog Catalog() => new() { Items = Items };

    /// <summary>The catalog envelope, each item <c>Paused</c> as <paramref name="store"/>'s controls say; without a store nothing is paused.</summary>
    internal async Task<ManagementExtensionCatalog> CatalogAsync(IDeliveryStore? store, CancellationToken ct)
    {
        if (store is null || Items.Count == 0)
        {
            return Catalog();
        }

        var paused = (await store.ControlsAsync(ct).ConfigureAwait(false)).Select(control => control.Subscriber).ToHashSet(StringComparer.Ordinal);

        return new ManagementExtensionCatalog { Items = [.. Items.Select(item => paused.Contains(item.Id) ? item with { Paused = true } : item)] };
    }

    /// <summary>The extensions that run on <paramref name="operation"/> (a policy wire name) of <paramref name="entity"/>.</summary>
    internal IReadOnlyList<ManagementExtension> RunningOn(string entity, string operation) =>
        [.. Items.Where(item => Covers(item, entity) && item.Operations.Contains(operation, StringComparer.Ordinal))];

    /// <summary>A kind's wire name, read off its own serialisation so the two cannot disagree.</summary>
    internal static string WireName(AlvoExtensionKind kind) => JsonSerializer.Serialize(kind).Trim('"');

    private static bool Covers(ManagementExtension item, string entity) =>
        item.Entities.Contains(entity, StringComparer.Ordinal)
        || item.Entities.Contains(EntityEvents.AllEntities, StringComparer.Ordinal);

    private static ManagementExtension[] Describe(ExtensionRegistry registry, bool exposeTypeNames) =>
        [.. registry.Handlers
            .OrderBy(handler => handler.Settings.Order)
            .ThenBy(handler => handler.Sequence)
            .Select(handler => HandlerDescription.Of(handler, exposeTypeNames))];
}
