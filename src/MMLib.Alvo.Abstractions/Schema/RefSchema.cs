namespace MMLib.Alvo.Schema;

/// <summary>Describes a reference/foreign key relationship in a field.</summary>
/// <param name="TargetEntity">The name of the entity being referenced.</param>
/// <param name="OnDelete">The referential integrity action on deletion.</param>
public sealed record RefSchema(string TargetEntity, OnDelete OnDelete);
