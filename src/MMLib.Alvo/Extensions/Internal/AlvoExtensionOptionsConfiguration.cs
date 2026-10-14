using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoExtensionOptions"/> from its section and refuses an unusable value at start.</summary>
/// <param name="configuration">The host's configuration, or <see langword="null"/> in a host that has none.</param>
internal sealed class AlvoExtensionOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoExtensionOptions>, IValidateOptions<AlvoExtensionOptions>
{
    internal const string MaxDepthKey = $"{AlvoExtensionOptions.SectionName}:{nameof(AlvoExtensionOptions.MaxDepth)}";

    private const int MinimumMaxDepth = 1;

    public void Configure(AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoExtensionOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.MaxDepth >= MinimumMaxDepth
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(MaxDepthRefusal(options.MaxDepth));
    }

    private static string MaxDepthRefusal(int configured) =>
        $"'{configured}' is not a usable {MaxDepthKey}. Set it (as an environment variable, "
        + $"{MaxDepthKey.Replace(":", "__", StringComparison.Ordinal)}) to at least {MinimumMaxDepth}: below that every "
        + "write an event handler or a timer makes would be refused as a loop.";
}
