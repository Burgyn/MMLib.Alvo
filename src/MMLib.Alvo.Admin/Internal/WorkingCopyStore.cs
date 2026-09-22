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
/// <para>
/// <b>Two things keep the dictionary honest, and both are about the key rather than the value.</b>
/// A caller the resolver refuses carries <see cref="AlvoContext.Anonymous"/>, whose user is the
/// reserved all-zero <see cref="UserId"/> — so keying on it would hand two different unresolvable
/// operators the <em>same</em> working copy, which is precisely the sharing the paragraph above
/// forbids. And nothing but a successful apply removes an entry, so a store that only ever grew
/// would hold every operator who ever opened an editor until the process restarted.
/// </para>
/// </remarks>
/// <param name="time">The clock, so the idle window is testable rather than wall-clock only.</param>
internal sealed class WorkingCopyStore(TimeProvider time)
{
    /// <summary>
    /// How long a copy nobody has touched is kept.
    /// </summary>
    /// <remarks>
    /// Long enough that an editor left open over lunch, or over a night, is still there in the
    /// morning; short enough that an operator who edited once and never came back is not held for
    /// the life of the process. An expiry a person can hit by taking a break would make the store
    /// worse than the circuit-scoped version it replaced.
    /// </remarks>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromDays(2);

    private readonly ConcurrentDictionary<UserId, Entry> _copies = new();

    /// <summary>This operator's working copy, created empty on first use.</summary>
    /// <remarks>
    /// A caller with no identity gets a copy that is never stored — private to this call, shared
    /// with nobody, and discarded when the screen lets go of it. They can compose whatever they
    /// like in it and the core will refuse the apply, which is the right shape: the refusal
    /// belongs to the operation, not to opening an editor.
    /// </remarks>
    /// <param name="user">Whose copy.</param>
    /// <returns>Their working copy.</returns>
    public WorkingCopy For(UserId user)
    {
        if (user.Value == Guid.Empty)
        {
            return new WorkingCopy();
        }

        EvictIdle();

        var entry = _copies.GetOrAdd(user, _ => new Entry(new WorkingCopy()));
        entry.LastTouched = time.GetUtcNow();
        return entry.Copy;
    }

    /// <summary>Forgets an operator's copy.</summary>
    /// <remarks>
    /// Called after an apply, once the copy has been re-taken from the new revision: keeping a copy
    /// that is identical to what is applied costs nothing but reads as a pending change.
    /// </remarks>
    /// <param name="user">Whose copy to forget.</param>
    public void Forget(UserId user) => _copies.TryRemove(user, out _);

    private void EvictIdle()
    {
        var cutoff = time.GetUtcNow() - IdleWindow;

        foreach (var (user, entry) in _copies)
        {
            if (entry.LastTouched < cutoff)
            {
                _copies.TryRemove(user, out _);
            }
        }
    }

    private sealed class Entry(WorkingCopy copy)
    {
        public WorkingCopy Copy { get; } = copy;

        public DateTimeOffset LastTouched { get; set; }
    }
}
