using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Gives every sign-in operation its contract in the OpenAPI document: tag <c>auth</c>, its operation id, a summary, its
/// body, every status with its headers, and the Bearer scheme where the route takes one — and publishes that scheme with
/// a description naming this host's token endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in operations are recognised by their endpoint names</b> (<c>auth.token</c>, …), which
/// <see cref="AccountsEndpoints"/> sets and which ASP.NET Core already holds unique across the host. The RFC 8414
/// path-inserted metadata URL is excluded from the description, so the document lists one metadata operation.
/// </para>
/// <para>
/// A document with no sign-in route — a host that defines a second document, or maps sign-in elsewhere — is left as it is.
/// </para>
/// </remarks>
/// <param name="options">The accounts options, for the token endpoint's path.</param>
internal sealed class AccountsDocumentTransformer(IOptions<AlvoAccountsOptions> options) : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var operations = SignInOperations(context);
        if (operations.Count == 0)
        {
            return Task.CompletedTask;
        }

        ProblemComponents.AddTo(document);
        AccountsSchemas.AddTo(document);
        AlvoDocumentTransformer.AddBearer(document, options.Value.Prefix.TrimEnd('/') + "/token");
        DescribeTag(document);
        foreach (var (description, operationId) in operations)
        {
            AccountsDocumentation.Describe(AlvoDocumentTransformer.FindOperation(document, description), operationId, document);
        }

        return Task.CompletedTask;
    }

    private static List<(ApiDescription Description, string OperationId)> SignInOperations(OpenApiDocumentTransformerContext context) =>
        [.. context.DescriptionGroups.SelectMany(group => group.Items)
            .Select(description => (Description: description, Name: NameOf(description)))
            .Where(entry => entry.Name is not null && AccountsDocumentation.Describes(entry.Name))
            .Select(entry => (entry.Description, entry.Name!))];

    private static string? NameOf(ApiDescription description) =>
        description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName;

    private static void DescribeTag(OpenApiDocument document)
    {
        if (document.Tags?.FirstOrDefault(tag => string.Equals(tag.Name, AccountsEndpoints.Tag, StringComparison.Ordinal)) is { } tag)
        {
            tag.Description =
                "End-user sign-in: exchange an identity provider's ID token for an Alvo session, refresh it, read and name "
                + "the signed-in person, sign out, and discover the issuer and its signing keys.";
        }
    }
}

/// <summary>
/// Attaches <see cref="AccountsDocumentTransformer"/> to every OpenAPI document the host defines, the way the core's
/// <c>AlvoOpenApiSetup</c> attaches its own: named, so <c>AddOpenApi()</c>'s <c>v1</c> gets it, and registered with
/// <c>TryAddEnumerable</c>, so calling <c>AddAlvoAccounts</c> twice enriches once.
/// </summary>
internal sealed class AccountsOpenApiSetup : IConfigureNamedOptions<OpenApiOptions>
{
    /// <inheritdoc/>
    public void Configure(string? name, OpenApiOptions options) => Configure(options);

    /// <inheritdoc/>
    public void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddDocumentTransformer<AccountsDocumentTransformer>();
    }
}
