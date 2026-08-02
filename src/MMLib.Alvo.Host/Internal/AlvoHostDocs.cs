using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace MMLib.Alvo.Host.Internal;

/// <summary>The host's docs decision: which document to emit, and what renders it.</summary>
/// <remarks>
/// <para>
/// <b>Registration order is transformer order.</b> Alvo's own document transformer appends its overview to
/// <c>info.description</c> rather than replacing it, so the host's <c>info</c> has to be written first — which
/// means <see cref="AddAlvoHostDocs"/> must be called before <c>AddAlvo</c>. The core deliberately never calls
/// <c>AddOpenApi</c> itself, because serving a document is a hosting decision.
/// </para>
/// <para>
/// <b>The route the page fetches is pinned to the route the host maps</b> rather than left to Scalar's default
/// pattern, so the two cannot drift apart into a docs page that renders an error against a document URL
/// nothing serves.
/// </para>
/// </remarks>
internal static class AlvoHostDocs
{
    private const string DocumentTitle = "Alvo";

    private const string DocumentDescription =
        "The Data API of one Alvo backend, generated from the project descriptor this host booted with. "
        + "Changing the descriptor changes this document; none of it is hand-written.";

    internal static void AddAlvoHostDocs(this IServiceCollection services) =>
        services.AddOpenApi(AlvoHost.OpenApiDocumentName, options => options.AddDocumentTransformer(Describe));

    internal static void MapAlvoHostDocs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi();
        endpoints.MapScalarApiReference(
            AlvoHost.ScalarPath,
            options => options.AddDocument(
                AlvoHost.OpenApiDocumentName, DocumentTitle, AlvoHost.OpenApiDocumentPath));
    }

    /// <summary>
    /// Writes the host's own <c>info</c>, the half of the document Alvo's transformer will not supply.
    /// </summary>
    /// <remarks>
    /// Without it the title is the entry assembly's name and the description is Alvo's overview alone, which
    /// names the mechanism but never says what this particular backend is.
    /// </remarks>
    private static Task Describe(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = DocumentTitle;
        document.Info.Version = AlvoHost.OpenApiDocumentName;
        document.Info.Description = DocumentDescription;
        return Task.CompletedTask;
    }
}
