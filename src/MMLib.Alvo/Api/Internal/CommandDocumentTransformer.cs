using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Commands.Internal;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Gives every command operation Alvo's shape: the operationId <c>commands.&lt;name&gt;</c>, the tag <c>commands</c>, the
/// refusals the command filter can answer with — referenced from the shared components, added here if the Data API added
/// none — and the anonymous-or-API-key security every Alvo route documents.
/// </summary>
/// <remarks>Runs after <see cref="AlvoDocumentTransformer"/>, so it adds only the components that one did not.</remarks>
/// <param name="auth">The auth options the API-key header's name comes from.</param>
/// <param name="resolvers">Says whether a person resolver is registered, which adds the Bearer alternative.</param>
internal sealed class CommandDocumentTransformer(IOptions<AlvoAuthOptions> auth, CallerResolvers resolvers)
    : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var commands = Commands(context);
        if (commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        ProblemComponents.AddTo(document);
        AlvoDocumentTransformer.AddCredential(document, auth.Value.HeaderName, resolvers.HasPerson);
        foreach (var (description, marker) in commands)
        {
            Enrich(document, description, marker, resolvers.HasPerson);
        }

        return Task.CompletedTask;
    }

    private static List<(ApiDescription Description, CommandEndpointMetadata Marker)> Commands(
        OpenApiDocumentTransformerContext context) =>
        [.. context.DescriptionGroups.SelectMany(group => group.Items)
            .Select(description => (Description: description, Marker: MarkerOf(description)))
            .Where(entry => entry.Marker is not null)
            .Select(entry => (entry.Description, entry.Marker!))];

    private static CommandEndpointMetadata? MarkerOf(ApiDescription description) =>
        description.ActionDescriptor.EndpointMetadata.OfType<CommandEndpointMetadata>().FirstOrDefault();

    private static void Enrich(
        OpenApiDocument document, ApiDescription description, CommandEndpointMetadata marker, bool person)
    {
        var operation = AlvoDocumentTransformer.FindOperation(document, description);
        operation.OperationId = CommandOperationIds.Of(marker.Name);
        operation.Security = AlvoDocumentTransformer.SecurityOf(document, person);
        Describe(operation, marker);
        operation.Responses ??= [];
        if (operation.RequestBody is not null)
        {
            BindingRefusals(operation.Responses);
        }

        foreach (var refusal in DataApiDocumentation.CommandRefusals(marker.Entity is not null))
        {
            AlvoDocumentTransformer.AddSharedRefusal(document, refusal);
            operation.Responses[refusal.Status.ToString(CultureInfo.InvariantCulture)] =
                new OpenApiResponseReference(refusal.SharedId!, document) { Description = refusal.SharedNarrowing };
        }
    }

    /// <summary>
    /// The two refusals ASP.NET Core's minimal APIs answer for a command that reads a body, before Alvo runs (Ruling 44):
    /// listed so a client sees them, inline because they are the framework's shape and not an Alvo problem document.
    /// </summary>
    private static void BindingRefusals(OpenApiResponses responses)
    {
        responses["400"] = new OpenApiResponse
        {
            Description =
                "The request body could not be bound to the command's input. Answered by ASP.NET Core's minimal APIs "
                + "before Alvo runs, in the host's own shape — not a problem document Alvo writes.",
        };
        responses["415"] = new OpenApiResponse
        {
            Description =
                "The request's 'Content-Type' is missing or not one of the media types this request body lists. Answered by "
                + "ASP.NET Core's minimal APIs before Alvo runs, in the host's own shape — not a problem document Alvo writes. "
                + "Send the body as one of the listed media types.",
        };
    }

    /// <summary>
    /// A summary, the request body's description and the bound row key's description, wherever the host left the operation
    /// without one — the ruleset refuses an undescribed operation, body or parameter.
    /// </summary>
    /// <remarks>
    /// Only what Alvo knows the meaning of is described: the operation, its body as "the command's input", and the
    /// <c>id</c> of a bound command. Any other route or query parameter is the host's, and so is the schema of its input
    /// type — a <c>[Description]</c> on the type describes it.
    /// </remarks>
    private static void Describe(OpenApiOperation operation, CommandEndpointMetadata marker)
    {
        if (string.IsNullOrEmpty(operation.Summary) && string.IsNullOrEmpty(operation.Description))
        {
            operation.Summary = marker.Entity is null
                ? $"Run the '{marker.Name}' command"
                : $"Run the '{marker.Name}' command on one '{marker.Entity}' row";
        }

        if (operation.RequestBody is OpenApiRequestBody body && string.IsNullOrEmpty(body.Description))
        {
            body.Description = $"The input of the '{marker.Name}' command.";
        }

        if (marker.Entity is { } entity && RowKey(operation) is { } key && string.IsNullOrEmpty(key.Description))
        {
            key.Description =
                $"The key of the '{entity}' row the command acts on. A row that does not exist and one the "
                + "caller's policy excludes are the same 404, so it cannot be used to prove a row exists.";
        }
    }

    /// <summary>
    /// The bound row's <c>{id}</c> path parameter — declared here when the handler does not bind it, because ApiExplorer
    /// then leaves it out while the route still carries it, and every path template parameter must be declared.
    /// </summary>
    private static OpenApiParameter? RowKey(OpenApiOperation operation)
    {
        if (operation.Parameters?.FirstOrDefault(IsRowKey) is { } existing)
        {
            return existing as OpenApiParameter;
        }

        var declared = new OpenApiParameter
        {
            Name = "id",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        };
        operation.Parameters ??= [];
        operation.Parameters.Add(declared);
        return declared;
    }

    private static bool IsRowKey(IOpenApiParameter parameter) =>
        parameter.In == ParameterLocation.Path && string.Equals(parameter.Name, "id", StringComparison.Ordinal);
}
