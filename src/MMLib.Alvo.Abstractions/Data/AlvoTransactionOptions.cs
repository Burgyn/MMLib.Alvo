namespace MMLib.Alvo.Data;

/// <summary>How one transaction unit runs: how long it may hold the database, and whether host write hooks are skipped.</summary>
public sealed class AlvoTransactionOptions
{
    internal const int MaxReasonLength = 500;

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    internal static TimeSpan MaxTimeout { get; } = TimeSpan.FromHours(1);

    private static readonly AlvoTransactionOptions _default = new();

    /// <summary>Gets how long the unit may run before it is cancelled and rolled back; 30 seconds unless set, at most one hour.</summary>
    /// <remarks>
    /// Measured on the host's <see cref="TimeProvider"/>. At the deadline the unit's token is cancelled, the unit rolls back
    /// as soon as no call through it is running, and the database is free again — work the delegate does after that is
    /// refused, and <c>TransactAsync</c> throws <see cref="TimeoutException"/>.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Gets a value indicating whether host write hooks are skipped for every write in the unit; declarative hooks and events never are.</summary>
    /// <remarks>Accepted only with a system context and a <see cref="Reason"/>.</remarks>
    public bool SuppressHostHooks { get; init; }

    /// <summary>Gets why host write hooks are suppressed: at most 500 characters, no control character.</summary>
    public string? Reason { get; init; }

    /// <summary>The options a unit runs with: <paramref name="options"/>, or the defaults, once checked against <paramref name="context"/>.</summary>
    /// <param name="options">The caller's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The caller opening the unit.</param>
    /// <returns>The checked options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not greater than zero and at most one hour.</exception>
    /// <exception cref="ArgumentException">Hooks are suppressed for a caller who is not the system, or without a usable reason.</exception>
    internal static AlvoTransactionOptions Validated(AlvoTransactionOptions? options, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var checkedOptions = options ?? _default;
        EnsureTimeout(checkedOptions.Timeout);
        EnsureReason(checkedOptions.Reason);
        EnsureSuppression(checkedOptions, context);

        return checkedOptions;
    }

#pragma warning disable CA2208 // Timeout and Reason are the properties of the options the caller handed in, so the ones to name.
    private static void EnsureTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout), timeout, "A unit's Timeout must be greater than zero and at most one hour: a unit holds the database's write lock while it runs.");
        }
    }

    private static void EnsureReason(string? reason)
    {
        if (reason is not null && (reason.Length > MaxReasonLength || reason.Any(char.IsControl)))
        {
            throw new ArgumentException(
                $"A unit's Reason is at most {MaxReasonLength} characters with no control character: it is written to one log field.", nameof(Reason));
        }
    }

    private static void EnsureSuppression(AlvoTransactionOptions options, AlvoContext context)
    {
        if (!options.SuppressHostHooks)
        {
            return;
        }

        if (context.ActorKind != AlvoActorKind.System)
        {
            throw new ArgumentException(
                "SuppressHostHooks is accepted only with a system context (AlvoContext.System): host write hooks may be skipped by the framework's own maintenance, never on a caller's behalf.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Reason))
        {
            throw new ArgumentException("SuppressHostHooks needs a Reason: skipping host write hooks is audited.", nameof(Reason));
        }
    }
#pragma warning restore CA2208
}
