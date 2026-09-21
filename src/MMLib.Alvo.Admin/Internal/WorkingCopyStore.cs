using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Everybody's unapplied edits, one working copy per operator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not simply scoped to the circuit.</b> A Blazor Server scope is a circuit, and a
/// circuit ends when the browser reloads the page. A working copy held there would mean that
/// pressing F5 — or opening a deep link in a second tab — silently discards every unapplied edit.
/// In a configuration tool that is a defect rather than a trade-off: the whole point of a working
/// copy is that a change can be composed over several screens before it is applied.
/// </para>
/// <para>
/// <b>Per operator, never shared.</b> One person's unapplied edits are theirs. Two administrators
/// editing at once each compose their own descriptor, and whoever applies second is refused by
/// <c>If-Match</c> rather than overwriting the first — which is the honest outcome, and the one a
/// shared copy would silently destroy.
/// </para>
/// <para>
/// <b>In memory, and that is stated rather than hidden.</b> A restart loses unapplied edits. The
/// alternative is a table of half-finished descriptors with nobody to clean it up, and a durable
/// draft is a feature with its own lifecycle questions — expiry, ownership, visibility — that this
/// build has not answered. A draft that survives a reload and not a deploy is the right first step.
/// </para>
/// </remarks>
internal sealed class WorkingCopyStore
{
    private readonly ConcurrentDictionary<UserId, WorkingCopy> _copies = new();

    /// <summary>This operator's working copy, created empty on first use.</summary>
    /// <param name="user">Whose copy.</param>
    /// <returns>Their working copy.</returns>
    public WorkingCopy For(UserId user) => _copies.GetOrAdd(user, _ => new WorkingCopy());

    /// <summary>Forgets an operator's copy.</summary>
    /// <remarks>
    /// Called after an apply, once the copy has been re-taken from the new revision: keeping a copy
    /// that is identical to what is applied costs nothing but reads as a pending change.
    /// </remarks>
    /// <param name="user">Whose copy to forget.</param>
    public void Forget(UserId user) => _copies.TryRemove(user, out _);
}
