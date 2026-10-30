using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Commands.Internal;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Gives every command operation Alvo's shape: the operationId <c>commands.&lt;name&gt;</c>, the tag <c>commands</c>, the
/// refusals the command filter can answer with — referenced from the shared components, added here if the Data API added
/// none — and the anonymous-or-API-key security every Alvo route documents.
/// </summary>
/// <remarks>Runs after <see cref="AlvoDocumentTransformer"/>, so it adds only the components that one did not.</remarks>
internal sealed class CommandDocumentTransformer(IOptions<AlvoAuthOptions> auth) : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var commands = Commands(context);
        if (commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        ProblemComponents.AddTo(document);
        AlvoDocumentTransformer.AddCredential(document, auth.Value.HeaderName);
        foreach (var (description, marker) in commands)
        {
            Enrich(document, description, marker);
        }

        return Task.CompletedTask;
    }

    private static List<(ApiDescription Description, CommandEndpointMetadata Marker)> Commands(OpenApiDocumentTransformerContext context) =>
        [.. context.DescriptionGroups.SelectMany(group => group.Items)
            .Select(description => (Description: description, Marker: description.ActionDescriptor.EndpointMetadata.OfType<CommandEndpointMetadata>().FirstOrDefault()))
            .Where(entry => entry.Marker is not null)
            .Select(entry => (entry.Description, entry.Marker!))];

    private static void Enrich(OpenApiDocument document, ApiDescription description, CommandEndpointMetadata marker)
    {
        var operation = AlvoDocumentTransformer.FindOperation(document, description);
        operation.OperationId = CommandOperationIds.Of(marker.Name);
        operation.Security = AlvoDocumentTransformer.SecurityOf(document);
        Describe(operation, marker);
        operation.Responses ??= [];
        if (operation.RequestBody is not null)
        {
            operation.Responses["400"] = new OpenApiResponse
            {
                Description = "The request body could not be bound to the command's input. Answered by ASP.NET Core's minimal APIs before Alvo runs, in the host's own shape — not a problem document Alvo writes.",
            };
        }

        foreach (var refusal in DataApiDocumentation.CommandRefusals(marker.Entity is not null))
        {
            AlvoDocumentTransformer.AddSharedRefusal(document, refusal);
            operation.Responses[refusal.Status.ToString(CultureInfo.InvariantCulture)] =
                new OpenApiResponseReference(refusal.SharedId!, document) { Description = refusal.SharedNarrowing };
        }
    }

    /// <summary>
    /// A summary, and the bound row key's description, wherever the host's own <c>WithSummary</c>/<c>WithDescription</c>
    /// left the operation without one — the ruleset refuses an undescribed operation or parameter.
    /// </summary>
    /// <remarks>
    /// Only the <c>id</c> of a bound command is described: Alvo knows what it names. Any other route or query parameter is
    /// the host's, and so is its description.
    /// </remarks>
    private static void Describe(OpenApiOperation operation, CommandEndpointMetadata marker)
    {
        if (string.IsNullOrEmpty(operation.Summary) && string.IsNullOrEmpty(operation.Description))
        {
            operation.Summary = marker.Entity is null
                ? $"Run the '{marker.Name}' command"
                : $"Run the '{marker.Name}' command on one '{marker.Entity}' row";
        }

        if (marker.Entity is not null
            && operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(IsRowKey) is { } key
            && string.IsNullOrEmpty(key.Description))
        {
            key.Description =
                $"The key of the '{marker.Entity}' row the command acts on. A row that does not exist and one the "
                + "caller's policy excludes are the same 404, so it cannot be used to prove a row exists.";
        }
    }

    private static bool IsRowKey(OpenApiParameter parameter) =>
        parameter.In == ParameterLocation.Path && string.Equals(parameter.Name, "id", StringComparison.Ordinal);
}
