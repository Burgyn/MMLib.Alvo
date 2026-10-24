using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoDeliveryOptions"/> from its section and refuses a concurrency no pool can run.</summary>
/// <param name="configuration">The host's configuration, or <see langword="null"/> in a host that has none.</param>
internal sealed class AlvoDeliveryOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoDeliveryOptions>, IValidateOptions<AlvoDeliveryOptions>
{
    internal const string MaxConcurrencyKey = $"{AlvoDeliveryOptions.SectionName}:{nameof(AlvoDeliveryOptions.MaxConcurrency)}";

    private const int MostConcurrency = 256;

    public void Configure(AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoDeliveryOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.MaxConcurrency is >= 1 and <= MostConcurrency
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(MaxConcurrencyRefusal(options.MaxConcurrency));
    }

    private static string MaxConcurrencyRefusal(int configured) =>
        $"'{configured}' is not a usable {MaxConcurrencyKey}. Set it (as an environment variable, "
        + $"{MaxConcurrencyKey.Replace(":", "__", StringComparison.Ordinal)}) to a number from 1 to {MostConcurrency}: "
        + "how many event-handler deliveries this process runs at once.";
}
