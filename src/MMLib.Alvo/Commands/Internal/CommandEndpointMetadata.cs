namespace MMLib.Alvo.Commands.Internal;

/// <summary>Marks an endpoint <c>MapAlvoCommand</c> mapped: the OpenAPI enrichment, the exception handler and the boot's gate check read it.</summary>
/// <param name="Name">The command's name.</param>
/// <param name="Entity">The entity it binds its row from, or null.</param>
internal sealed record CommandEndpointMetadata(string Name, string? Entity);
