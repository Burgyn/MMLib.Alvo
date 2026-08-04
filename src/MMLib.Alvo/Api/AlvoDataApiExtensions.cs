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
    /// <summary>Configures the generated Data API, which <c>AddAlvo</c> has already registered.</summary>
    /// <remarks>
    /// <para>
    /// <b>The Data API is on by default, so this call is configuration and nothing else.</b> It is the point
    /// of the framework: a registration a host has to ask for is a trap rather than a choice, and asking for
    /// it was one of the ordering obligations this seam exists to remove. <c>AddAlvo</c> registers the
    /// services, and nothing is reachable over HTTP until
    /// <c>MapAlvoDataApi</c> — or <c>MapAlvo</c> — is called, which is a separate seam by design
    /// (<c>docs/architecture/extensibility.md</c> rule 10).
    /// </para>
    /// <para>
    /// Additive and idempotent (<c>Add{Thing}</c> in the fixed verb taxonomy, rule 7): calling it twice is not
    /// a duplicate, and a second call carrying no <paramref name="configure"/> does not undo the first. Order
    /// does not matter either — the default registration contributes no configure action of its own, so a
    /// host's value wins whether it was written before or after <c>AddAlvo</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Configures <see cref="AlvoApiOptions"/> — the route prefix and the paging limits.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder AddDataApi(this IAlvoBuilder builder, Action<AlvoApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder;
    }
}
