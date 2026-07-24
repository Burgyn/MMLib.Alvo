using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// A snapshot of the schema last applied to a project's database, as persisted by
/// <see cref="IAppliedSchemaStore"/>.
/// </summary>
/// <param name="Schema">The applied <see cref="SchemaModel"/>.</param>
/// <param name="DescriptorJson">The raw descriptor JSON the schema was derived from.</param>
/// <param name="Revision">The monotonically increasing revision number of this snapshot.</param>
/// <param name="UpdatedAt">When this snapshot was written.</param>
/// <remarks>
/// This is the PR-A precursor to PR-B's append-only descriptor version store: today there is a
/// single current row per project, not a history.
/// </remarks>
public sealed record AppliedSchema(SchemaModel Schema, string DescriptorJson, int Revision, DateTimeOffset UpdatedAt);
