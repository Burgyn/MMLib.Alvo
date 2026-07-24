using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// One immutable entry in a project's append-only descriptor history. Code-first and runtime
/// apply both append here; the latest revision is the "current" side of the migration diff.
/// </summary>
/// <param name="Schema">The applied <see cref="SchemaModel"/> at this revision.</param>
/// <param name="DescriptorJson">The raw descriptor JSON this revision was derived from.</param>
/// <param name="Revision">The monotonically increasing revision number (first applied revision is 1).</param>
/// <param name="CreatedAt">When this revision was appended.</param>
/// <param name="Author">Who appended it (null for code-first / system).</param>
/// <param name="Reason">Optional human/agent-supplied reason.</param>
/// <param name="RolledBackFrom">If this revision was produced by a rollback, the revision it restored; otherwise null.</param>
public sealed record DescriptorVersion(
    SchemaModel Schema,
    string DescriptorJson,
    int Revision,
    DateTimeOffset CreatedAt,
    string? Author = null,
    string? Reason = null,
    int? RolledBackFrom = null);
