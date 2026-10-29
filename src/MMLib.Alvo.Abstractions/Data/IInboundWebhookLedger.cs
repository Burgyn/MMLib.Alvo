namespace MMLib.Alvo.Data;

/// <summary>
/// The per-receiver record of Standard Webhooks ids already handled, so a sender's retry does not run a handler twice.
/// Implemented by the relational drivers; the core reaches it through <c>InternalsVisibleTo</c>. A claim is leased and
/// fenced like a command's (Ruling 36): completing or releasing applies only while the row carries the claim's token.
/// </summary>
internal interface IInboundWebhookLedger
{
    /// <summary>Claims <paramref name="webhookId"/> on <paramref name="receiver"/> for one run.</summary>
    Task<WebhookClaimResult> ClaimAsync(
        string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks a claimed id handled; answers false when the claim was taken over.</summary>
    Task<bool> CompleteAsync(
        string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Gives up a claim the run still holds, so the sender's retry runs it again.</summary>
    Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken);

    /// <summary>Deletes every id received before <paramref name="receivedBefore"/>; answers how many.</summary>
    Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken);
}

/// <summary>What claiming a webhook id found.</summary>
internal enum WebhookClaim
{
    /// <summary>This delivery may run.</summary>
    Claimed,

    /// <summary>A delivery with this id was handled already.</summary>
    Completed,

    /// <summary>A delivery with this id is still being handled.</summary>
    InFlight,
}

/// <summary>A webhook claim's outcome, and its fencing token when it was granted.</summary>
internal readonly record struct WebhookClaimResult(WebhookClaim State, string? Token = null);

/// <summary>The inbound webhook ledger's fixed bounds.</summary>
internal static class InboundWebhookLimits
{
    /// <summary>How long a claim holds an id before a retry may take it over.</summary>
    internal static TimeSpan Lease { get; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a handled id is remembered.</summary>
    internal static TimeSpan Retention { get; } = TimeSpan.FromDays(7);

    /// <summary>How often the framework ledgers are swept.</summary>
    internal static TimeSpan SweepInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>The longest <c>webhook-id</c> accepted, in UTF-8 bytes.</summary>
    internal const int MaxIdBytes = 255;
}
