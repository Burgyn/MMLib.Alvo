namespace MMLib.Alvo.Migrations;

/// <summary>
/// Port for a project's append-only descriptor history: every code-first or runtime apply adds a
/// new <see cref="DescriptorVersion"/> rather than overwriting the previous one, so the full
/// audit trail (and rollback target) is always available. Supersedes PR-A's single-row
/// <see cref="IAppliedSchemaStore"/>, which only ever kept the current snapshot.
/// </summary>
public interface IDescriptorVersionStore
{
    /// <summary>Gets the most recently appended (highest-revision) version for a project, if any.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current version, or <see langword="null"/> if no version has been appended yet.</returns>
    Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default);

    /// <summary>Gets one specific historical revision for a project.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="revision">The revision number to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching version, or <see langword="null"/> if that revision does not exist.</returns>
    Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default);

    /// <summary>Lists a project's full history, ordered from oldest to newest revision.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The project's history; empty if no version has been appended yet.</returns>
    Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default);

    /// <summary>
    /// Appends a new version iff <paramref name="expectedRevision"/> matches the current revision
    /// (0 when the history is empty) — an optimistic-lock conditional append. History is never
    /// mutated: a successful append only ever adds a new, immutable row.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="candidate">
    /// The version to append. Its <see cref="DescriptorVersion.Revision"/> is ignored; the
    /// inserted row's revision is always <paramref name="expectedRevision"/> + 1.
    /// </param>
    /// <param name="expectedRevision">The revision the caller expects to currently be latest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended version, with its assigned <see cref="DescriptorVersion.Revision"/>.</returns>
    /// <exception cref="DescriptorConcurrencyException">
    /// <paramref name="expectedRevision"/> no longer matches the store's current revision.
    /// </exception>
    Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default);
}
