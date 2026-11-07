using Microsoft.Extensions.Logging;

using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Automations;

/// <summary>The Automations page: the applied descriptor's automation rules, read-only.</summary>
public partial class Automations
{
    private IReadOnlyList<AutomationRow>? _rows;
    private ManagementCapabilities? _capabilities;
    private string? _problem;

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _capabilities = await Gateway.CapabilitiesAsync(CancellationToken.None);
            var descriptor = await Gateway.DescriptorAsync(CancellationToken.None);
            using var document = JsonDocument.Parse(descriptor.DescriptorJson);
            _rows = AutomationRows.From(document.RootElement);
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException or JsonException)
        {
            Unreadable(Logger, ex);
            _problem = "The applied descriptor could not be read, so its automation rules cannot be listed.";
        }
    }

    private static string DeliveriesHref(AutomationRow rule) =>
        $"{AdminPaths.Operations}?subscriber={Uri.EscapeDataString(rule.DeliveryPrefix)}";

    /// <summary>Fixed text and the exception only: a descriptor is never written to a log.</summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Reading the descriptor for the Automations page failed")]
    private static partial void Unreadable(ILogger logger, Exception exception);
}
