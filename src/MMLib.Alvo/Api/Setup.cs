using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api;

/// <summary>Registers the generated Data API's services: its options, its route catalog, and its authorization filter.</summary>
internal static class ApiSetup
{
    /// <summary>
    /// Adds <see cref="AlvoApiOptions"/> (validated at startup by
    /// <see cref="AlvoApiOptionsValidator"/>), the <see cref="EntityRouteCatalog"/> that answers which
    /// entities get routes, and the <see cref="AlvoContextFilterFactory"/> that builds one
    /// <see cref="AlvoContextFilter"/> per mapped endpoint.
    /// </summary>
    /// <remarks>
    /// Called from <c>AddAlvo</c> rather than only from <c>AddDataApi</c>, for the same reason
    /// <c>AddAlvoAuth</c>/<c>AddAlvoRules</c> are: registering a service exposes nothing. Nothing here
    /// is reachable until a host calls <c>MapAlvoDataApi</c>, which is a separate seam by design
    /// (<c>docs/architecture/extensibility.md</c> rule 10) — so <c>AddDataApi</c> exists to
    /// <em>configure</em> the feature and to make it discoverable, not to switch it on.
    /// <c>TryAdd*</c> throughout, so a host can substitute any of the three; the validator is added
    /// with <c>TryAddEnumerable</c>, exactly as <c>AddAlvoAuth</c> adds its own, so registering
    /// <c>AddAlvo</c> twice does not report every failure twice.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>It deliberately does not call <c>AddOpenApi</c>.</b> Whether there is an OpenAPI document at all, how
    /// many, under what names, and whether any of them is served is entirely the host's decision — Alvo only
    /// registers <see cref="AlvoOpenApiSetup"/>, which enriches whichever documents the host defines, in either
    /// order. Calling <c>AddOpenApi</c> here would give a host that wants none a document service, an
    /// ApiExplorer and a set of options it never asked for; and it ran <em>twice</em>, because this method is
    /// called from both <c>AddAlvo</c> and <c>AddDataApi</c>.
    /// </para>
    /// <para>
    /// <b>It is deliberately not in <c>AddDataApi</c> either.</b> That seam is optional — a host may register
    /// <c>AddAlvo</c> and call <c>MapAlvoDataApi</c> without ever configuring the feature — so putting the
    /// registration there would make enrichment silently depend on a call a host had no reason to make. Here it
    /// applies to every host that registers Alvo at all, and costs a host with no document nothing but one
    /// options setup that is never resolved.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the API services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoApi(this IServiceCollection services)
    {
        services.AddOptions<AlvoApiOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoApiOptions>, AlvoApiOptionsValidator>());
        services.TryAddSingleton<EntityRouteCatalog>();
        services.TryAddSingleton<AlvoContextFilterFactory>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<OpenApiOptions>, AlvoOpenApiSetup>());
        return services;
    }
}

/// <summary>
/// Attaches <see cref="AlvoDocumentTransformer"/> to <b>every</b> OpenAPI document this host defines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered with <c>TryAddEnumerable</c>, and that is a fix rather than a habit.</b>
/// <see cref="ApiSetup.AddAlvoApi"/> runs twice by design — <c>AddAlvo</c> calls it, and so does
/// <c>AddDataApi</c> — and <c>AddOpenApi(configure)</c> is additive, so passing the transformer that way
/// registered it twice and the document was enriched twice: the overview paragraph appeared verbatim in
/// <c>info.description</c> twice over. <c>TryAddEnumerable</c> deduplicates on the implementation type, which
/// is exactly the shape every other registration in that method already uses, and
/// <c>OpenApiDocumentTests.The_overview_is_appended_once_however_often_alvo_is_registered</c> is what keeps
/// the duplication from coming back — the snapshot alone made drift reviewable without saying which of the two
/// documents was right.
/// </para>
/// <para>
/// <b><see cref="IConfigureNamedOptions{TOptions}"/> rather than <see cref="IConfigureOptions{TOptions}"/>,
/// because a plain one would never run.</b> Options' factory invokes an unnamed setup only for the
/// <em>default</em> name, and <c>AddOpenApi()</c>'s document is named <c>v1</c> — so the transformer would have
/// been registered and silently never applied. Configuring every name is also the behaviour worth having: an
/// embedded host that defines its own document gets Alvo's endpoints enriched in it too, and the transformer
/// returns immediately for a document that carries none.
/// </para>
/// </remarks>
internal sealed class AlvoOpenApiSetup : IConfigureNamedOptions<OpenApiOptions>
{
    /// <inheritdoc/>
    public void Configure(string? name, OpenApiOptions options) => Configure(options);

    /// <inheritdoc/>
    public void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddDocumentTransformer<AlvoDocumentTransformer>();
    }
}
