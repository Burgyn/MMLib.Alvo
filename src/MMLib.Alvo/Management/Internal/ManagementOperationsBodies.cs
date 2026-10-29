using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The body of a redeliver: why, if the caller says.</summary>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementReasonBody(string? Reason);

/// <summary>The body of a pause or a resume.</summary>
/// <param name="Paused">Whether the handler should be paused; required.</param>
/// <param name="Reason">Kept on the pause and in the audit.</param>
internal sealed record ManagementPausedBody(bool? Paused, string? Reason);

/// <summary>The body of switching an extension off or on.</summary>
/// <param name="Enabled">Whether it should run; required.</param>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementEnabledBody(bool? Enabled, string? Reason);

/// <summary>The body of a replay; the key travels in the <c>Idempotency-Key</c> header.</summary>
/// <param name="Subscriber">The handler; required.</param>
/// <param name="Status">Which finished deliveries; <c>dead</c> when absent.</param>
/// <param name="From">The earliest event time included.</param>
/// <param name="To">The event time the window ends before.</param>
/// <param name="DryRun">Whether to only count.</param>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementReplayBody(string? Subscriber, string? Status, DateTimeOffset? From, DateTimeOffset? To, bool DryRun, string? Reason)
{
    /// <summary>The members this body does not read, kept so the route refuses them rather than ignores them (Ruling 28).</summary>
    /// <remarks>A misspelled <c>dry_run</c> ignored would turn a preview into a committed replay.</remarks>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>The names of the members this body does not read, in the order they were sent; empty when there are none.</summary>
    internal IReadOnlyList<string> UnknownNames => Unknown is null ? [] : [.. Unknown.Keys];

    /// <summary>The port's request, carrying the key read off the header.</summary>
    /// <param name="key">The single <c>Idempotency-Key</c> presented, or <see langword="null"/>.</param>
    internal ManagementReplayRequest ToRequest(string? key) => new()
    {
        Subscriber = Subscriber!,
        Status = Status ?? "dead",
        From = From,
        To = To,
        DryRun = DryRun,
        Reason = Reason,
        IdempotencyKey = key,
    };
}
