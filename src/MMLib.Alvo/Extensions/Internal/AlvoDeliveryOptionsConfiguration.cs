using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoDeliveryOptions"/> from its section and refuses a concurrency no pool can run, each such one at once.</summary>
/// <param name="configuration">The host's configuration, or <see langword="null"/> in a host that has none.</param>
internal sealed class AlvoDeliveryOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoDeliveryOptions>, IValidateOptions<AlvoDeliveryOptions>
{
    internal const string MaxConcurrencyKey = $"{AlvoDeliveryOptions.SectionName}:{nameof(AlvoDeliveryOptions.MaxConcurrency)}";
    internal const string MaxJobConcurrencyKey = $"{AlvoDeliveryOptions.SectionName}:{nameof(AlvoDeliveryOptions.MaxJobConcurrency)}";

    /// <summary>
    /// The undocumented knob a crash harness lowers so a descriptor action's lease ends inside its patience; nothing else sets
    /// it, so it is read here rather than published as a property.
    /// </summary>
    internal const string ActionTimeoutKey = $"{AlvoDeliveryOptions.SectionName}:ActionTimeout";

    private const int MostConcurrency = 256;

    public void Configure(AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoDeliveryOptions.SectionName).Bind(options);
        if (configuration?.GetValue<TimeSpan?>(ActionTimeoutKey) is { } actionTimeout)
        {
            options.ActionTimeout = actionTimeout;
        }
    }

    public ValidateOptionsResult Validate(string? name, AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var refusals = new List<string>();
        if (!Usable(options.MaxConcurrency))
        {
            refusals.Add(Refusal(MaxConcurrencyKey, options.MaxConcurrency, "event-handler deliveries"));
        }

        if (!Usable(options.MaxJobConcurrency))
        {
            refusals.Add(Refusal(MaxJobConcurrencyKey, options.MaxJobConcurrency, "scheduled-job and data-migration runs"));
        }

        if (options.ActionTimeout <= TimeSpan.Zero || options.ActionTimeout > AlvoHandlerOptions.MaxTimeout)
        {
            refusals.Add($"'{options.ActionTimeout}' is not a usable {ActionTimeoutKey}: it must be positive and at most {AlvoHandlerOptions.MaxTimeout}.");
        }

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    private static bool Usable(int concurrency) => concurrency is >= 1 and <= MostConcurrency;

    private static string Refusal(string key, int configured, string what) =>
        $"'{configured}' is not a usable {key}. Set it (as an environment variable, "
        + $"{key.Replace(":", "__", StringComparison.Ordinal)}) to a number from 1 to {MostConcurrency}: "
        + $"how many {what} this process runs at once.";
}
