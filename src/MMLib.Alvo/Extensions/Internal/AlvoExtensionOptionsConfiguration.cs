using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoExtensionOptions"/> from its section and refuses an unusable value at start.</summary>
/// <param name="configuration">The host's configuration, or <see langword="null"/> in a host that has none.</param>
internal sealed class AlvoExtensionOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoExtensionOptions>, IValidateOptions<AlvoExtensionOptions>
{
    internal const string CelFunctionBudgetKey = $"{AlvoExtensionOptions.SectionName}:{nameof(AlvoExtensionOptions.CelFunctionBudget)}";

    internal const string MaxDepthKey = $"{AlvoExtensionOptions.SectionName}:{nameof(AlvoExtensionOptions.MaxDepth)}";

    private const int MinimumMaxDepth = AlvoChainDepthLimit.MinimumMaxDepth;

    public void Configure(AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoExtensionOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> refusals = [];
        if (options.MaxDepth < MinimumMaxDepth)
        {
            refusals.Add(MaxDepthRefusal(options.MaxDepth));
        }

        if (options.CelFunctionBudget <= TimeSpan.Zero || options.CelFunctionBudget > AlvoHookOptions.MaxTimeout)
        {
            refusals.Add(BudgetRefusal(options.CelFunctionBudget));
        }

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    private static string MaxDepthRefusal(int configured) =>
        $"'{configured}' is not a usable {MaxDepthKey}. Set it (as an environment variable, "
        + $"{MaxDepthKey.Replace(":", "__", StringComparison.Ordinal)}) to at least {MinimumMaxDepth}: below that every "
        + "write an event handler or a timer makes would be refused as a loop.";

    private static string BudgetRefusal(TimeSpan configured) =>
        $"'{configured}' is not a usable {CelFunctionBudgetKey}. Set it (as an environment variable, "
        + $"{CelFunctionBudgetKey.Replace(":", "__", StringComparison.Ordinal)}) to more than zero and at most 30 s, for "
        + "example 00:00:00.250: host functions run inside the write's transaction, which must not wait on them longer.";
}
