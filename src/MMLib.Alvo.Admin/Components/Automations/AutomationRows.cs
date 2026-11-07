using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Automations;

/// <summary>One action of an automation rule, as the Automations page lists it.</summary>
/// <param name="Type">The action's type, such as <c>webhook</c> or <c>entity.update</c>.</param>
/// <param name="Target">What it acts on — the endpoint, the template and recipient, the function, the entity — or empty.</param>
internal sealed record AutomationActionRow(string Type, string Target);

/// <summary>One automation rule, as the Automations page lists it.</summary>
/// <param name="Name">The rule's name, its key under <c>automation</c>.</param>
/// <param name="Enabled">Whether the rule runs; <c>enabled</c> defaults to true.</param>
/// <param name="Trigger">What fires it, in words: <c>When entity.orders.created</c>, <c>Every day at 02:00 UTC</c>.</param>
/// <param name="Condition">Its CEL condition, or <see langword="null"/>.</param>
/// <param name="Actions">Its actions, in the order they are declared.</param>
internal sealed record AutomationRow(
    string Name, bool Enabled, string Trigger, string? Condition, IReadOnlyList<AutomationActionRow> Actions)
{
    /// <summary>The prefix every delivery of this rule's actions carries as its subscriber id: <c>/automation/{name}/actions#</c>.</summary>
    /// <remarks>
    /// A rule's action ids are the rule's actions pointer plus a content hash, with no index, so the prefix names the rule's
    /// deliveries — every action's — and never another rule's: <c>/automation/a/actions#</c> is not a prefix of
    /// <c>/automation/ab/…</c>.
    /// </remarks>
    public string DeliveryPrefix => $"/automation/{Name}/actions#";
}

/// <summary>The descriptor's <c>automation</c> block, read for the Automations page.</summary>
/// <remarks>Read-only: a rule is edited in the descriptor editor or through the assistant (HX-15 Decision 14).</remarks>
internal static class AutomationRows
{
    /// <summary>One row per rule, in declaration order.</summary>
    /// <param name="descriptor">The applied descriptor.</param>
    /// <returns>The rows; empty when the descriptor declares no <c>automation</c>.</returns>
    public static IReadOnlyList<AutomationRow> From(JsonElement descriptor)
    {
        if (descriptor.ValueKind != JsonValueKind.Object
            || !descriptor.TryGetProperty("automation", out var automation)
            || automation.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. automation.EnumerateObject().Where(rule => rule.Value.ValueKind == JsonValueKind.Object).Select(Row)];
    }

    private static AutomationRow Row(JsonProperty rule)
    {
        var value = rule.Value;
        var enabled = !value.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
        var condition = value.TryGetProperty("condition", out var cel) && cel.ValueKind == JsonValueKind.String ? cel.GetString() : null;
        var actions = value.TryGetProperty("actions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Action).ToList()
            : [];

        return new AutomationRow(rule.Name, enabled, Trigger(value), condition, actions);
    }

    private static string Trigger(JsonElement rule)
    {
        if (!rule.TryGetProperty("trigger", out var trigger) || trigger.ValueKind != JsonValueKind.Object)
        {
            return "No trigger";
        }

        if (Text(trigger, "event") is { } pattern)
        {
            return $"When {pattern}";
        }

        return Text(trigger, "schedule") is { } cron ? CronWords.Of(cron) : "No trigger";
    }

    private static AutomationActionRow Action(JsonElement action)
    {
        var type = Text(action, "type") ?? "unknown";
        var target = type switch
        {
            "webhook" => Text(action, "endpoint"),
            "email" => Join(Text(action, "template"), Text(action, "to")),
            "function" => Text(action, "name"),
            "entity.update" => Join(Text(action, "entity"), Text(action, "recordId") is { } id ? $"record {id}" : "new record"),
            "http.call" => Text(action, "url"),
            _ => null,
        };

        return new AutomationActionRow(type, target ?? string.Empty);
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} → {second}";

    private static string? Text(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>A five-field UTC cron expression in words, for the common shapes; any other is shown as written.</summary>
internal static class CronWords
{
    private static readonly string[] _days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>The expression in words, such as <c>Every day at 02:00 UTC</c>, or <c>Cron 0 2 1-5 * * (UTC)</c>.</summary>
    /// <param name="cron">The expression, as the descriptor writes it.</param>
    /// <returns>One sentence.</returns>
    public static string Of(string cron)
    {
        ArgumentNullException.ThrowIfNull(cron);
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 5 ? Words(fields) ?? Verbatim(cron) : Verbatim(cron);
    }

    private static string? Words(string[] f)
    {
        if (f is ["*", "*", "*", "*", "*"])
        {
            return "Every minute";
        }

        if (f[0].StartsWith("*/", StringComparison.Ordinal) && Number(f[0][2..]) is { } step && f[1..].All(field => field == "*"))
        {
            return $"Every {step} minutes";
        }

        if (Number(f[0]) is not { } minute || f[3] != "*")
        {
            return null;
        }

        if (f[1] == "*" && f[2] == "*" && f[4] == "*")
        {
            return string.Create(CultureInfo.InvariantCulture, $"Every hour at minute {minute}");
        }

        return Number(f[1]) is { } hour ? Daily(f, At(hour, minute)) : null;
    }

    private static string? Daily(string[] f, string at) => (f[2], f[4]) switch
    {
        ("*", "*") => $"Every day at {at}",
        ("*", var day) when Number(day) is { } weekday && weekday is >= 0 and <= 7 => $"Every {_days[weekday % 7]} at {at}",
        (var date, "*") when Number(date) is { } day && day is >= 1 and <= 31 =>
            string.Create(CultureInfo.InvariantCulture, $"On day {day} of every month at {at}"),
        _ => null,
    };

    private static string At(int hour, int minute) => string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00} UTC");

    private static int? Number(string field) =>
        int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Verbatim(string cron) => $"Cron {cron} (UTC)";
}
