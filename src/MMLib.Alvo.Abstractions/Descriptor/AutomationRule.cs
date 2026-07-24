using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// An event–condition–action automation rule (schema <c>$defs/automationRule</c>).
/// Runs post-commit from the outbox, durable, with retries.
/// </summary>
public sealed record AutomationRule
{
    /// <summary>Human-readable description of the rule.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the rule is active; defaults to <see langword="true"/> when omitted.</summary>
    public bool? Enabled { get; init; }

    /// <summary>What fires the rule: an event pattern or a cron schedule.</summary>
    public required AutomationTrigger Trigger { get; init; }

    /// <summary>Optional CEL condition gating the actions.</summary>
    public string? Condition { get; init; }

    /// <summary>How the rule dispatches over affected rows; defaults to <see cref="DeliveryMode.PerItem"/> when omitted.</summary>
    public DeliveryMode? Delivery { get; init; }

    /// <summary>Actions to run, in order, when the rule fires.</summary>
    public required IReadOnlyList<AutomationAction> Actions { get; init; }

    /// <summary>Extension keys (<c>x-*</c>) preserved verbatim through apply and export.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// The trigger of an automation rule (schema <c>automationRule.trigger</c>):
/// exactly one of <see cref="Event"/> or <see cref="Schedule"/> is set.
/// </summary>
public sealed record AutomationTrigger
{
    /// <summary>Event pattern that fires the rule (e.g. <c>entity.deals.updated</c>).</summary>
    public string? Event { get; init; }

    /// <summary>Cron expression (5 fields, UTC) that fires the rule.</summary>
    public string? Schedule { get; init; }
}
