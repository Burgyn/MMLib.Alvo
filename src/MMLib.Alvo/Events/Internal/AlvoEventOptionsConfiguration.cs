using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using System.Net;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The single owner of the <see cref="AlvoEventOptions.SectionName"/> configuration section: it binds the
/// section and refuses a value the dispatcher could not run on, naming the configuration key and a value that
/// would have worked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binding and validating in one type, exactly as <c>AlvoSchemaOptionsConfiguration</c> does</b>, because a
/// refusal has to quote the key an operator sets and the key set is the binder's own vocabulary. Two types
/// would be two lists of key names, and a message naming a key nothing binds is worse than no message.
/// </para>
/// <para>
/// <see cref="IConfiguration"/> is resolved <b>optionally</b> (a nullable constructor parameter supplied by a
/// factory registration, never <c>BuildServiceProvider</c>): a plain console host embedding Alvo need not have
/// registered configuration at all, and its absence means "no section", not a failure.
/// </para>
/// <para>
/// <b>Refused at startup rather than at the first claim</b> (<c>extensibility.md</c> rule 5). The dispatcher
/// runs off the startup thread and contains its own failures, so a bad value discovered inside the pump would
/// be one log line in a host that otherwise looks healthy — the failure mode the whole option set exists to
/// avoid.
/// </para>
/// </remarks>
/// <param name="configuration">The ambient configuration, or <see langword="null"/> when the host registered none.</param>
internal sealed class AlvoEventOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoEventOptions>, IValidateOptions<AlvoEventOptions>
{
    /// <summary>The configuration key of <see cref="AlvoEventOptions.PollInterval"/>.</summary>
    internal const string PollIntervalKey =
        $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.PollInterval)}";

    /// <summary>The configuration key of <see cref="AlvoEventOptions.BatchSize"/>.</summary>
    internal const string BatchSizeKey = $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.BatchSize)}";

    /// <summary>The configuration key of <see cref="AlvoEventOptions.MaxAttempts"/>.</summary>
    internal const string MaxAttemptsKey =
        $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.MaxAttempts)}";

    /// <summary>The configuration key of <see cref="AlvoEventOptions.ClaimLease"/>.</summary>
    internal const string ClaimLeaseKey = $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.ClaimLease)}";

    /// <summary>The configuration key of <see cref="AlvoEventOptions.WebhookAllowedNetworks"/>.</summary>
    internal const string WebhookAllowedNetworksKey =
        $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.WebhookAllowedNetworks)}";

    /// <summary>The environment-variable spelling of <see cref="WebhookAllowedNetworksKey"/>, without an index.</summary>
    internal static string WebhookAllowedNetworksVariable { get; } = EnvironmentVariable(WebhookAllowedNetworksKey);

    /// <inheritdoc/>
    public void Configure(AlvoEventOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        configuration?.GetSection(AlvoEventOptions.SectionName).Bind(options);
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoEventOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> refusals = [.. Refusals(options), .. NetworkRefusals(options.WebhookAllowedNetworks)];

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    private static IEnumerable<string> NetworkRefusals(IList<string> networks) =>
        networks
            .Select((network, index) => (network, index))
            .Where(entry => !IsNetwork(entry.network))
            .Select(entry => AllowedNetworkRefusal(entry.network, entry.index));

    /// <summary>
    /// Whether <paramref name="entry"/> is CIDR notation whose address is the network's own base address.
    /// </summary>
    /// <remarks>
    /// <see cref="IPNetwork.TryParse(string?, out IPNetwork)"/> on .NET 10 silently masks host bits, so
    /// <c>10.0.0.1/8</c> would admit all of <c>10.0.0.0/8</c> when the operator may have meant one host. An
    /// allow-list entry that is wider than it reads is refused rather than guessed at.
    /// </remarks>
    private static bool IsNetwork(string entry)
    {
        var prefix = entry.IndexOf('/', StringComparison.Ordinal);

        return prefix > 0
            && IPNetwork.TryParse(entry, out var network)
            && IPAddress.TryParse(entry.AsSpan(0, prefix), out var address)
            && address.Equals(network.BaseAddress);
    }

    private static IEnumerable<string> Refusals(AlvoEventOptions options)
    {
        if (options.BatchSize < MinimumBatchSize)
        {
            yield return BatchSizeRefusal(options.BatchSize);
        }

        if (options.MaxAttempts < MinimumMaxAttempts)
        {
            yield return MaxAttemptsRefusal(options.MaxAttempts);
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            yield return PollIntervalRefusal(options.PollInterval);
        }

        if (options.ClaimLease <= options.PollInterval)
        {
            yield return ClaimLeaseRefusal(options);
        }
    }

    private const int MinimumBatchSize = 1;
    private const int MinimumMaxAttempts = 1;

    private static string BatchSizeRefusal(int configured) =>
        $"'{configured}' is not a usable {BatchSizeKey}. Set it (as an environment variable, "
        + $"{EnvironmentVariable(BatchSizeKey)}) to at least {MinimumBatchSize}: a batch of that size claims "
        + "nothing on every tick, so no event is ever delivered while the dispatcher reports itself healthy.";

    private static string MaxAttemptsRefusal(int configured) =>
        $"'{configured}' is not a usable {MaxAttemptsKey}. Set it (as an environment variable, "
        + $"{EnvironmentVariable(MaxAttemptsKey)}) to at least {MinimumMaxAttempts}: the ceiling is compared "
        + "against an attempt count that already includes the claim being made, so a smaller one leaves every "
        + "event unclaimable.";

    private static string PollIntervalRefusal(TimeSpan configured) =>
        $"'{configured}' is not a usable {PollIntervalKey}. Set it (as an environment variable, "
        + $"{EnvironmentVariable(PollIntervalKey)}) to a positive interval such as '00:00:01': it is how long "
        + "the pump waits after finding nothing, so zero spins a CPU on an empty queue.";

    private static string ClaimLeaseRefusal(AlvoEventOptions options) =>
        $"'{options.ClaimLease}' is not a usable {ClaimLeaseKey} beside a {PollIntervalKey} of "
        + $"'{options.PollInterval}'. Set it (as an environment variable, "
        + $"{EnvironmentVariable(ClaimLeaseKey)}) to a longer interval than the poll interval, such as "
        + "'00:05:00': the lease is how long a claim holds, so one shorter than the interval re-claims an entry "
        + "that is still in flight on the very next tick — a duplicate delivery per tick rather than "
        + "at-least-once delivery.";

    private static string AllowedNetworkRefusal(string configured, int index) =>
        $"'{configured}' is not a usable {WebhookAllowedNetworksKey}:{index}. Set it (as an environment variable, "
        + $"{WebhookAllowedNetworksVariable}__{index}) to a network in CIDR notation with no host bits past its "
        + "prefix, such as '10.20.0.0/16' or 'fd00:20::/32': an entry nothing can parse would leave the internal "
        + "receiver it was meant for refused on every delivery.";

    private static string EnvironmentVariable(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
