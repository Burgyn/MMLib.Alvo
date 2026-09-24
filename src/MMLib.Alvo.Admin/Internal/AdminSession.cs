using MMLib.Alvo.Admin.Components.Schema;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Who the operator is on this circuit, and the working copy that is theirs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plumbing every screen that draws the working copy used to write by hand</b>
/// (docs/architecture/admin-dashboard-review.md, F-8): resolve the caller, key the store by their user, take
/// the copy from the applied descriptor the first time anybody needs it, and follow its
/// <see cref="WorkingCopy.Changed"/> without leaking a subscription. Eight components did the first, five
/// the take, and four followed the event in four different shapes.
/// </para>
/// <para>
/// <b>A service the component composes, not a base class it inherits.</b> A base class of a public Razor
/// component has to be public itself, which would turn this plumbing into the package's contract. Scoped,
/// because in Blazor Server a scope is a circuit, like the two gateways it sits on.
/// </para>
/// </remarks>
/// <param name="gateway">The management surface, for the applied descriptor a copy is taken from.</param>
/// <param name="data">Resolves the signed-in operator into the caller Alvo authorizes.</param>
/// <param name="copies">Every operator's working copy.</param>
internal sealed class AdminSession(ManagementGateway gateway, DataGateway data, WorkingCopyStore copies)
{
    /// <summary>The operator's own caller — re-resolved on every call, for the reason <see cref="DataGateway.ContextAsync"/> gives.</summary>
    /// <param name="ct">The cancellation token.</param>
    public ValueTask<AlvoContext> CallerAsync(CancellationToken ct) => data.ContextAsync(ct);

    /// <summary>This operator's working copy as it stands, loaded or not.</summary>
    /// <remarks>
    /// For what watches or replaces the copy and never reads the applied descriptor through it — the shell's
    /// count, the discard sheet, the assistant's proposal. Taking the copy there would read the descriptor on
    /// every screen, including for an operator who may not read it. The store is keyed by user, so the copy is
    /// never another administrator's.
    /// </remarks>
    /// <param name="ct">The cancellation token.</param>
    public async Task<WorkingCopy> CopyAsync(CancellationToken ct)
        => copies.For((await CallerAsync(ct).ConfigureAwait(false)).User);

    /// <summary>This operator's working copy, taken from the applied descriptor when nothing has taken it yet.</summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task<WorkingCopy> WorkingCopyAsync(CancellationToken ct)
    {
        var copy = await CopyAsync(ct).ConfigureAwait(false);
        await EnsureLoadedAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    /// <summary>Takes <paramref name="copy"/> from the applied descriptor, unless it already holds a document.</summary>
    /// <remarks>
    /// <para>
    /// <b>Never re-taken while it is loaded</b>: a loaded copy may carry the operator's unapplied edits, and
    /// re-taking it would discard them the moment they navigated. Asked again after the read, and in the same step
    /// as the take (<see cref="WorkingCopy.TakeIfUnloaded"/>), because another screen of the same operator may
    /// have taken it — and edited it — during the await, or between a second look and the take.
    /// </para>
    /// <para>
    /// Separate from <see cref="WorkingCopyAsync"/> for the screens that resolve the copy before a read that
    /// may fail, and take it only once that read succeeded.
    /// </para>
    /// </remarks>
    /// <param name="copy">The copy to load.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task EnsureLoadedAsync(WorkingCopy copy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (copy.Loaded)
        {
            return;
        }

        var descriptor = await gateway.DescriptorAsync(ct).ConfigureAwait(false);
        copy.TakeIfUnloaded(descriptor.DescriptorJson, descriptor.Revision);
    }

    /// <summary>Follows <paramref name="copy"/> until the returned handle or <paramref name="lifetime"/> ends it.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not subscribed at all when <paramref name="lifetime"/> has already ended</b> — a component disposed
    /// during the await that resolved the copy. Nothing would unsubscribe it, and the copy outlives every
    /// circuit that shows it.
    /// </para>
    /// <para>
    /// <b>Every change is marshalled through <paramref name="invoke"/></b>, the component's own
    /// <c>InvokeAsync</c>: the edit that raised it may be another tab's, on another circuit's thread.
    /// </para>
    /// </remarks>
    /// <param name="copy">The copy to follow.</param>
    /// <param name="lifetime">The component's lifetime; its end unsubscribes.</param>
    /// <param name="invoke">The component's <c>InvokeAsync</c>.</param>
    /// <param name="onChanged">What the component does when the copy moved, on its renderer's thread.</param>
    /// <returns>A handle that unsubscribes earlier, for a component that follows a different copy later.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Composed through the injected session, like the rest of it.")]
    public IDisposable Follow(
        WorkingCopy copy, Func<Func<Task>, Task> invoke, Func<Task> onChanged, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(onChanged);

        return lifetime.IsCancellationRequested
            ? Following.None
            : new Following(copy, () => _ = invoke(onChanged), lifetime);
    }

    /// <inheritdoc cref="Follow(WorkingCopy, Func{Func{Task}, Task}, Func{Task}, CancellationToken)"/>
    public IDisposable Follow(
        WorkingCopy copy, Func<Func<Task>, Task> invoke, Action onChanged, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        return Follow(copy, invoke, () =>
        {
            onChanged();
            return Task.CompletedTask;
        }, lifetime);
    }

    /// <summary>One subscription to <see cref="WorkingCopy.Changed"/>, ended once by whichever comes first.</summary>
    private sealed class Following : IDisposable
    {
        public static readonly IDisposable None = new Following();

        private readonly WorkingCopy? _copy;
        private readonly Action? _raise;
        private readonly CancellationTokenRegistration _ending;
        private int _ended;

        public Following(WorkingCopy copy, Action raise, CancellationToken lifetime)
        {
            _copy = copy;
            _raise = raise;
            _copy.Changed += _raise;
            _ending = lifetime.Register(Dispose);
        }

        private Following() => _ended = 1;

        /* Unregister rather than Dispose the registration: this may be running inside the lifetime's own
           cancellation callback, and Dispose would wait for that callback to finish. */
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _copy!.Changed -= _raise;
                _ending.Unregister();
            }
        }
    }
}
