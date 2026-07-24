namespace MMLib.Alvo.Schema;

/// <summary>Describes the complete schema of a backend-as-a-service project.</summary>
/// <param name="Entities">The entities defined in the schema.</param>
public sealed record SchemaModel(IReadOnlyList<EntitySchema> Entities);
