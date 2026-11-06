using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// Where a command's <c>Idempotency-Key</c> is claimed, committed and answered, and where a command that opened no unit
/// writes its completion. Implemented by the relational drivers; the core reaches it through <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// A key moves claimed → committed → answered. A claim is autocommit, leased and <b>fenced</b>: each grant mints a random
/// token, and the commit mark, the answer and a release apply only while the row still carries that token (Ruling 36) —
/// so a run that outlived its lease, whose key another run took over, can neither commit nor free the newer claim. The
/// rendered answer is kept, which <see cref="AlvoIdempotency"/>'s Data API contract never does; it is bound to one
/// credential (Ruling 34) and swept a day after its claim (Ruling 41).
/// </remarks>
internal interface ICommandLedger
{
    /// <summary>
    /// Claims <paramref name="key"/> for one run — granting a fencing token — or says why it cannot be run.
    /// </summary>
    Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records the answer of a run whose unit already committed, while the run still holds the claim.</summary>
    Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken);

    /// <summary>Gives up an uncommitted claim the run still holds, so a retry runs the command again.</summary>
    Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken);

    /// <summary>
    /// For a run that opened no unit: appends <paramref name="completion"/> and, with a key, marks it committed and answered —
    /// in one transaction, which is refused (<see cref="CommandClaimLostException"/>) when the run no longer holds the claim.
    /// </summary>
    Task CompleteAsync(
        AlvoFrameworkEnvelope completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Forgets every key claimed before <paramref name="claimedBefore"/>; answers how many.</summary>
    Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// One command request's key: its scope (command, caller and credential), the caller's key, the request's fingerprint and,
/// once claimed, the fencing token the claim granted.
/// </summary>
internal sealed record CommandKey(string Scope, string Key, string Fingerprint)
{
    /// <summary>Gets the fencing token of the claim this run holds; null until claimed.</summary>
    internal string? Token { get; init; }
}

/// <summary>
/// A run's answer as it is replayed: the status, the JSON body, if any, and the body's media type when it is not plain
/// <c>application/json</c> — a problem document's <c>application/problem+json</c>.
/// </summary>
internal sealed record CommandAnswer(int Status, string? Body, string? MediaType = null)
{
    /// <summary>Gets a value indicating whether the answer is a 2xx.</summary>
    internal bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>What claiming a key found.</summary>
internal enum CommandClaimState
{
    /// <summary>This request may run: <see cref="CommandClaim.Token"/> carries its fencing token.</summary>
    Claimed,

    /// <summary>A run with this key answered; replay its answer.</summary>
    Answered,

    /// <summary>A run with this key is still being processed.</summary>
    InFlight,

    /// <summary>A run with this key committed, and its answer was never recorded.</summary>
    Unanswered,

    /// <summary>The key was spent on a different request.</summary>
    Mismatch,
}

/// <summary>The outcome of a claim.</summary>
internal sealed record CommandClaim(CommandClaimState State, CommandAnswer? Answer = null, string? Token = null)
{
    /// <summary>Gets the claimable state a lookup reports before the store takes the claim.</summary>
    internal static CommandClaim Claimable { get; } = new(CommandClaimState.Claimed);

    internal static CommandClaim InFlight { get; } = new(CommandClaimState.InFlight);

    internal static CommandClaim Unanswered { get; } = new(CommandClaimState.Unanswered);

    internal static CommandClaim Mismatch { get; } = new(CommandClaimState.Mismatch);

    internal static CommandClaim Granted(string token) => new(CommandClaimState.Claimed, Token: token);

    internal static CommandClaim Answered(CommandAnswer answer) => new(CommandClaimState.Answered, answer);
}

/// <summary>A run tried to commit or answer under a claim another run took over after its lease ran out.</summary>
internal sealed class CommandClaimLostException()
    : InvalidOperationException(
        "This command run's claim on its Idempotency-Key was taken over after its 15-minute lease ran out, so it may "
        + "not commit: its unit is rolled back. A handler must finish within the lease.");

/// <summary>The ledger's fixed bounds.</summary>
internal static class CommandLedgerLimits
{
    /// <summary>How long a claim holds a key before a retry may take it over.</summary>
    internal static TimeSpan Lease { get; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a key and its answer are kept after their claim (the IETF draft's suggested expiry).</summary>
    internal static TimeSpan Retention { get; } = TimeSpan.FromHours(24);

    /// <summary>The largest answer body recorded; a larger one is not replayable.</summary>
    internal const int MaxAnswerBytes = 65_536;
}
