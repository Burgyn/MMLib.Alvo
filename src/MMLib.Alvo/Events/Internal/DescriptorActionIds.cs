using MMLib.Alvo.Descriptor;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The one authority on a descriptor action's subscriber id: the JSON pointer of the point that declares it, <c>#</c>, and
/// eight lower-case hex digits of a SHA-256 over what it does — <c>/entities/orders/hooks/afterCreate#1f0c9a2e</c>,
/// <c>/automation/notify-sales/actions#77ab03d4</c>, and for a cron rule's schedule <c>/automation/nightly-close#c01d5e9a</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No sibling index.</b> Reordering a point's actions keeps every id; editing one makes a new subscriber, so the old one's
/// pending deliveries are never claimed and never run the edited action — the apply's
/// <see cref="Extensions.Internal.DescriptorActionSweep"/> cancels them. Two identical actions at one point are told apart
/// by an ordinal suffix, <c>-2</c> onwards, in declaration order.
/// </para>
/// <para>
/// <b>Stable across processes.</b> Each part is written through the descriptor's own source-generated serializer — the
/// polymorphic <c>type</c> discriminator included — and the parts are joined by a unit separator, so the same declaration
/// hashes the same on every replica and after every restart.
/// </para>
/// </remarks>
internal static class DescriptorActionIds
{
    private const char PartSeparator = '\u001f';
    private const int HashLength = 8;

    /// <summary>An after-hook's id at <paramref name="point"/>, such as <c>/entities/orders/hooks/afterCreate</c>.</summary>
    internal static string Hook(string point, string? condition, AutomationAction action) =>
        For(point, Text(condition), Text(action));

    /// <summary>The id of one action of automation rule <paramref name="rule"/>.</summary>
    internal static string RuleAction(string rule, AutomationTrigger trigger, string? condition, AutomationAction action) =>
        For($"/automation/{rule}/actions", Text(trigger), Text(condition), Text(action));

    /// <summary>The id of a cron rule's trigger-only schedule.</summary>
    internal static string Schedule(string rule, AutomationTrigger trigger) => For($"/automation/{rule}", Text(trigger));

    /// <summary>The id of the data migration that re-plans entity <paramref name="entity"/>'s timers, over each timer's definition.</summary>
    internal static string TimerReplan(string entity, string[] timers) => For($"/entities/{entity}/timers", timers);

    /// <summary>Whether <paramref name="subscriber"/> is a descriptor action rather than a registered extension.</summary>
    internal static bool IsDescriptorAction(string subscriber) => subscriber.StartsWith('/');

    /// <summary><paramref name="id"/>, or — when <paramref name="taken"/> already holds it — the first free ordinal of it.</summary>
    internal static string Unique(string id, ISet<string> taken)
    {
        var unique = id;
        for (var ordinal = 2; !taken.Add(unique); ordinal++)
        {
            unique = $"{id}-{ordinal}";
        }

        return unique;
    }

    private static string For(string point, params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(PartSeparator, parts)));

        return $"{point}#{Convert.ToHexStringLower(hash)[..HashLength]}";
    }

    private static string Text(AutomationAction action) => Compact(action, AlvoDescriptorJsonContext.Default.AutomationAction);

    private static string Text(AutomationTrigger trigger) => Compact(trigger, AlvoDescriptorJsonContext.Default.AutomationTrigger);

    /// <summary>The value written without indentation, so no platform's line ending reaches the hash.</summary>
    private static string Compact<T>(T value, JsonTypeInfo<T> type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            JsonSerializer.Serialize(writer, value, type);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Text(string? condition) => condition ?? string.Empty;
}
