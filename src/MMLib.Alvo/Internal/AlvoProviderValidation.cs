using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Internal;

/// <summary>
/// Fail-fast startup check (spec §0 principle 5, secure-by-default) asserting that a database
/// provider was selected inside <c>AddAlvo(...)</c>. Runs as part of
/// <c>AddOptions&lt;AlvoOptions&gt;().ValidateOnStart()</c> — a host that never selects a provider
/// fails loudly at startup with an actionable fix, instead of a <see cref="NullReferenceException"/>
/// the first time something deep in the pipeline resolves <see cref="ISchemaMigrator"/>.
/// </summary>
internal sealed class AlvoProviderValidation(IServiceProvider serviceProvider) : IValidateOptions<AlvoOptions>
{
    internal const string NoProviderRegisteredMessage =
        "No Alvo database provider is registered. Call UseSqlite(...) or UsePostgreSql(...) inside AddAlvo(...).";

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoOptions options) =>
        serviceProvider.GetService<ISchemaMigrator>() is null
            ? ValidateOptionsResult.Fail(NoProviderRegisteredMessage)
            : ValidateOptionsResult.Success;
}
