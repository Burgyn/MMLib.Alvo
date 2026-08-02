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
    /// An update. Its pre-image read provably never precedes a key change — the row id is framework-owned
    /// and a caller-supplied <c>id</c> is rejected before the read — so the weaker lock mode applies.
    /// </summary>
    Update,

    /// <summary>A delete, which removes the row's key and therefore needs the stronger lock mode.</summary>
    Delete,
}
