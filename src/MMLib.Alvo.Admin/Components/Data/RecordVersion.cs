using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// The version a record's save and delete carry, so a write never silently overwrites one somebody else made since
/// the record was opened (docs/todo-admin.md §8d item 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>The port's own channel, <see cref="AlvoPrecondition"/>, and the version it was read with.</b> The Data API
/// turns <c>If-Match</c> into exactly this value (<c>RowVersionETag</c>), so the grid guards a write the way
/// <c>/api</c> does, over the same comparison inside the write transaction. The version is the <c>updated_at</c> the
/// record was read with — never one this process computed, which <see cref="AlvoPrecondition"/>'s remarks explain
/// would not survive its own round trip.
/// </para>
/// <para>
/// <b>None where the entity keeps none.</b> <see cref="AlvoPrecondition.EnsureSupported"/> refuses a precondition on
/// an entity with no version column, so an unaudited record is written unconditionally and the editor says, once,
/// that the last write wins there. An audited record read without a version it can use (unreachable for a physical
/// entity, whose managed columns cannot be masked; possible for a host-assembled schema) is written without one too,
/// and the editor says that instead — it never claims the entity is unaudited when it asked for audit.
/// </para>
/// </remarks>
internal static class RecordVersion
{
    /// <summary>The headline of a write that lost to another: a stale version, or a record that is gone.</summary>
    public const string ConflictTitle = "This record changed since you opened it";

    /// <summary>What the editor of a record written without a version says, once.</summary>
    public const string LastWriteWinsSentence =
        "Last write wins here: this entity keeps no version of a record (it is not audited), so saving or deleting "
        + "replaces whatever anyone else wrote since you opened it.";

    /// <summary>The precondition a write of the record read as <paramref name="values"/> carries, or none.</summary>
    /// <param name="entity">The record's entity.</param>
    /// <param name="values">The record as it was read.</param>
    public static AlvoPrecondition? Of(EntitySchema entity, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return AlvoManagedColumns.VersionColumn(entity) is { } column
            && values.TryGetValue(column, out var version) && version is DateTimeOffset read
                ? new AlvoPrecondition(read)
                : null;
    }

    /// <summary>What the editor of an audited record read without a usable version says, once.</summary>
    public const string UnreadableVersionSentence =
        "This record carries no readable version, so this save cannot be checked against a concurrent change: it "
        + "replaces whatever anyone else wrote since you opened it.";

    /// <summary>
    /// The one sentence an editor says about a write that carries no version, or <see langword="null"/> when it carries
    /// one.
    /// </summary>
    /// <param name="entity">The record's entity.</param>
    /// <param name="values">The record as it was read.</param>
    public static string? Caveat(EntitySchema entity, IReadOnlyDictionary<string, object?> values)
    {
        if (!LastWriteWins(entity, values))
        {
            return null;
        }

        return AlvoManagedColumns.VersionColumn(entity) is null ? LastWriteWinsSentence : UnreadableVersionSentence;
    }

    /// <summary>Whether a write of this record carries no version, so it replaces whatever is stored.</summary>
    /// <param name="entity">The record's entity.</param>
    /// <param name="values">The record as it was read.</param>
    public static bool LastWriteWins(EntitySchema entity, IReadOnlyDictionary<string, object?> values)
        => Of(entity, values) is null;

    /// <summary>
    /// Whether <paramref name="problem"/> is a write that lost to another one, which Reload answers: the version was
    /// stale, or the record is gone — deleted meanwhile, or no longer admitted by the read rule, which the port
    /// answers the same way on purpose.
    /// </summary>
    /// <param name="problem">The refusal on show, or <see langword="null"/>.</param>
    public static bool IsConflict(AdminProblem? problem)
        => problem?.Exception is AlvoPreconditionFailedException or AlvoRecordNotFoundException;
}
