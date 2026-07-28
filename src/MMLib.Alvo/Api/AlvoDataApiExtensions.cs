using MMLib.Alvo;
using MMLib.Alvo.Api;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The generated Data API's registration seam, owned by the core package. In
/// <c>Microsoft.Extensions.DependencyInjection</c> per the extensibility rules
/// (<c>docs/architecture/extensibility.md</c> rule 1), like every other Alvo builder extension; the
/// endpoint seam is a separate class in <c>Microsoft.AspNetCore.Builder</c>, per rule 10.
/// </summary>
public static class AlvoDataApiExtensions
{
    /// <summary>Registers the generated Data API's services.</summary>
    /// <remarks>
    /// Additive and idempotent (<c>Add{Thing}</c> in the fixed verb taxonomy). The services themselves
    /// are already registered by <c>AddAlvo</c> — registering a service exposes nothing — so this method
    /// exists to <em>configure</em> the feature and to make it discoverable beside the rest of the
    /// builder. Nothing is reachable over HTTP until <c>MapAlvoDataApi</c> is called.
    /// </remarks>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Configures <see cref="AlvoApiOptions"/> — the route prefix and the paging limits.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder AddDataApi(this IAlvoBuilder builder, Action<AlvoApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAlvoApi();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder;
    }
}
