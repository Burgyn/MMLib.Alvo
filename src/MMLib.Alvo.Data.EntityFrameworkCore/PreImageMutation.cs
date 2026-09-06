namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The mutation a locked pre-image read precedes. Two members, because exactly two operations read a row
/// they are about to change: <see cref="MMLib.Alvo.Rules.DataOperation"/>'s <c>List</c>, <c>Get</c> and
/// <c>Create</c> have no pre-image at all, and a lock clause is meaningless for them.
/// </summary>
/// <remarks>
/// A distinct enum rather than <see cref="MMLib.Alvo.Rules.DataOperation"/> so the illegal state is
/// unrepresentable: taking the policy vocabulary here would leave three of its five members to be refused
/// at runtime by every dialect, including third-party ones, and a caller could still write the refused
/// call and compile. This makes the same mistake a compile error instead — which matters most for the
/// dialects Alvo will never see.
/// </remarks>
public enum PreImageMutation
{
    /// <summary>
    /// An update, or the replace branch of a create-or-replace. Its pre-image read provably never precedes a
    /// key change — no write path ever puts the row key in its setter list, so the row this read locks keeps
    /// the id it was found by — and the weaker lock mode therefore applies.
    /// <para>
    /// <b>The reason used to be "a caller-supplied <c>id</c> is rejected before the read", and #105 made that
    /// false</b>: create-or-replace takes the row's id straight from the path. The conclusion survives the
    /// premise, because what the weaker mode needs is that the key does not <em>move</em>, not that the caller
    /// never named it.
    /// </para>
    /// </summary>
    Update,

    /// <summary>A delete, which removes the row's key and therefore needs the stronger lock mode.</summary>
    Delete,
}
