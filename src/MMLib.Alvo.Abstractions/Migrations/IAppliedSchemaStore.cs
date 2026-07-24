namespace MMLib.Alvo.Migrations;

/// <summary>
/// Port for persisting the schema snapshot last applied to a project's database, making the
/// code-first diff (<see cref="ISchemaMigrator"/>) snapshot-primary and idempotent across
/// restarts: the "current" side of a plan comes from here, not from a live introspection.
/// </summary>
public interface IAppliedSchemaStore
{
    /// <summary>Gets the currently applied schema snapshot for a project, if any.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current snapshot, or <see langword="null"/> if none has been saved yet.</returns>
    Task<AppliedSchema?> GetCurrentAsync(string project, CancellationToken ct = default);

    /// <summary>Saves (upserts) the currently applied schema snapshot for a project.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="snapshot">The snapshot to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct = default);
}
