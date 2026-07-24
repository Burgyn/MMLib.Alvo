namespace MMLib.Alvo.Schema;

/// <summary>Describes an index on one or more fields of an entity.</summary>
/// <param name="Fields">The field names composing the index.</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
public sealed record IndexSchema(IReadOnlyList<string> Fields, bool Unique);
