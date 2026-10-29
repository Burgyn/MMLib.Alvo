using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Rules;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>What one open unit is made of: the context that opened its connection, the connection, the transaction, the write turn and the DI scope.</summary>
/// <param name="Root">The context that opened the connection and began the transaction; disposed when the unit ends.</param>
/// <param name="Connection">The one connection every call of the unit runs on.</param>
/// <param name="Transaction">The one transaction every call of the unit joins.</param>
/// <param name="Turn">This database's write turn, held from the unit's <c>BEGIN</c> to its end.</param>
/// <param name="Scope">The unit's DI scope — not the request's.</param>
internal sealed record WriteUnitParts(
    AlvoDataContext Root, DbConnection Connection, IDbContextTransaction Transaction, IDisposable Turn, AsyncServiceScope Scope);

/// <summary>
/// One transaction unit (spec 2026-10-14 §4.5): one connection and transaction that every call joins under its own
/// savepoint, the write turn held from <c>BEGIN</c> to the end, one DI scope, a deadline, and the first failure that
/// poisons it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call at a time — per level.</b> A <see cref="DbConnection"/> runs one command at a time, so a second call made
/// while one is running is refused, and poisons the unit: the delegate's result can no longer be the one it meant. A call
/// made <em>by</em> the running call — on its own execution flow, as a hook running inside a write will (HX-09) — is let in
/// under its own savepoint, one at a time among its siblings; each call records itself in an <see cref="AsyncLocal{T}"/>
/// frame for exactly that (Ruling 30).
/// </para>
/// <para>
/// <b>A nested call ends inside the call that made it (Ruling 54).</b> When a call returns, its frame is finished: a call
/// it started and did not await, still running then, poisons the unit and is refused when it returns; one that starts
/// after is refused, and poisons the unit too. Otherwise such a call would run outside the one-at-a-time gate — beside a
/// sibling, or beside the <c>COMMIT</c> — and a row could commit without the outbox event its write was still adding.
/// </para>
/// <para>
/// <b>Poisoned means it cannot commit.</b> <see cref="Poison"/> records the first failure; every later call is refused, and
/// <see cref="CommitAsync"/> rolls back and throws that failure — so a delegate that catches it still cannot commit.
/// </para>
/// <para>
/// <b>The commit decision is atomic (Ruling 46).</b> <see cref="CommitAsync"/> takes the gate and reads the failure under
/// one lock: from then on no call is admitted and a later <see cref="Poison"/> is ignored, so a decided commit commits. A
/// call still running at that moment — one the delegate started and did not await — fails the unit: it is cancelled,
/// awaited, and the unit rolls back and throws an <see cref="InvalidOperationException"/> naming it.
/// </para>
/// <para>
/// <b>A decided commit is not cancellable (Ruling 50).</b> The <c>COMMIT</c> runs with <see cref="CancellationToken.None"/>:
/// a caller's cancel landing while it runs could otherwise report as cancelled a transaction the engine committed — the
/// outcome Ruling 47 exists to rule out. A caller's cancel that landed before the decision is part of it: the unit rolls
/// back and throws an <see cref="OperationCanceledException"/>, even when nothing observed the token.
/// </para>
/// <para>
/// <b>The deadline ends the unit, not the delegate.</b> At <see cref="AlvoTransactionOptions.Timeout"/> the unit's token is
/// cancelled and the unit is poisoned with a <see cref="TimeoutException"/>; as soon as no call is running it rolls back
/// and lets go of the connection and the write turn — a delegate that ignores its token no longer holds the database. The
/// deadline runs on the host's <see cref="TimeProvider"/>, and a deadline that fired before the commit decision wins over
/// the commit (Ruling 51).
/// </para>
/// <para>
/// <b>Every function passed to <see cref="RunAsync{T}"/> honours its token.</b> The deadline's rollback, a commit that
/// found a call still running and <see cref="DisposeAsync"/> all cancel the unit's calls and then wait for them, so that
/// bound is what keeps a unit from holding the write turn past its end (HX-09's hook budget must keep it true). The
/// token is cancelled only while a call is in flight — a unit that ended cleanly fires no cancellation callback — and a
/// callback that throws never stands between the unit and its rollback.
/// </para>
/// <para>
/// <b>A committed unit reports success (Ruling 47).</b> Once <c>COMMIT</c> succeeded, a failure disposing the transaction
/// or the context is logged as a warning after the write turn is released, not thrown: the data is committed, and a
/// caller that retried on the exception would write it twice.
/// </para>
/// <para>
/// <b>Open until released.</b> <see cref="IsOpen"/> stays true from opening until <see cref="DisposeAsync"/> — the end of
/// <c>TransactAsync</c> — so the flow refuses other data-port calls for the delegate's whole run, even after a deadline
/// rolled the transaction back; work that outlives <c>TransactAsync</c> is not refused.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2213:Disposable fields should be disposed",
    Justification = "SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which this type never does; disposing it would race a deadline still waiting for the unit's turn to roll it back.")]
internal sealed partial class WriteUnit : IAlvoFlowUnit, IAsyncDisposable
{
    private const string SavepointPrefix = "alvo_unit_";

    private static readonly AsyncLocal<CallFrame?> _frame = new();

    private readonly WriteUnitParts _parts;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _onDeadline;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly Lock _gate = new();
    private IBeforeHookRunner? _hooks;
    private Exception? _failure;
    private bool _ended;
    private bool _ending;
    private bool _finished;
    private bool _released;
    private int _savepoints;

    /// <summary>Initializes an open unit over <paramref name="parts"/>.</summary>
    /// <param name="parts">The opened connection, the begun transaction, the turn and the scope.</param>
    /// <param name="caller">The caller who opened it.</param>
    /// <param name="provenance">The unit's provenance, which every write inside it reports.</param>
    /// <param name="timeout">How long the unit may run.</param>
    /// <param name="time">The clock the deadline runs on.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it cancels every call of the unit.</param>
    internal WriteUnit(
        WriteUnitParts parts, AlvoContext caller, AlvoProvenance provenance, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        _parts = parts;
        Caller = caller;
        Provenance = provenance;
        _timeout = timeout;
        _deadline = new CancellationTokenSource(timeout, time);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        _onDeadline = _deadline.Token.Register(static state => ((WriteUnit)state!).OnDeadline(), this);
    }

    /// <summary>Gets the connection every call of the unit runs on.</summary>
    internal DbConnection Connection => _parts.Connection;

    /// <summary>Gets the transaction every call of the unit joins.</summary>
    internal DbTransaction Transaction => _parts.Transaction.GetDbTransaction();

    /// <summary>Gets the caller who opened the unit.</summary>
    internal AlvoContext Caller { get; }

    /// <summary>Gets the unit's provenance: the caller's effective one, with a correlation id of the unit's own when it had none.</summary>
    internal AlvoProvenance Provenance { get; }

    /// <summary>Gets the unit's DI scope, shared by everything the unit resolves.</summary>
    internal IServiceProvider Services => _parts.Scope.ServiceProvider;

    /// <summary>Gets the declarative hook pipeline a write inside the unit runs, resolved from the unit's scope once, on first use.</summary>
    internal IBeforeHookRunner Hooks => _hooks ??= Services.GetRequiredService<IBeforeHookRunner>();

    /// <summary>Gets the token every call of the unit runs under: the caller's, ended at the deadline.</summary>
    internal CancellationToken Token => _lifetime.Token;

    /// <summary>Gets a value indicating whether the unit's deadline has passed.</summary>
    internal bool TimedOut => _deadline.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether the unit's transaction has committed or rolled back, and the unit has let go of its
    /// connection and write turn.
    /// </summary>
    internal bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                return _finished;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_released;
            }
        }
    }

    /// <inheritdoc/>
    public void Poison(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (!_ended)
            {
                _failure ??= failure;
            }
        }
    }

    /// <summary>Makes this unit the one open on the current flow, with its provenance, until the returned scope is disposed.</summary>
    /// <remarks>
    /// The one place the driver puts a unit on the flow (<c>AlvoFlow.EnterUnit</c>): a call joins the unit only from a flow
    /// that carries it (<see cref="UnitSavepoint"/>), and the data port refuses every other call there.
    /// </remarks>
    /// <returns>A scope whose disposal restores what was on the flow before.</returns>
    internal IDisposable EnterFlow() => AlvoFlow.EnterUnit(this, Provenance);

    /// <summary>The next savepoint name of this unit: <c>alvo_unit_1</c>, <c>alvo_unit_2</c>, …</summary>
    internal string NextSavepoint() =>
        SavepointPrefix + Interlocked.Increment(ref _savepoints).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs one call of the unit: alone among its siblings, under the unit's token, refused once the unit failed or ended.
    /// A call made from inside a running call of this unit is a nested call: it waits on its parent's gate, not the unit's,
    /// and must end before its parent returns (Ruling 54).
    /// </summary>
    /// <typeparam name="T">What the call answers.</typeparam>
    /// <param name="call">The call, given the token it must pass to every statement.</param>
    /// <param name="cancellationToken">The call's own token.</param>
    /// <returns>What <paramref name="call"/> answered.</returns>
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        EnsureUsable();
        var parent = _frame.Value is { } frame && frame.Unit == this ? frame : null;
        Enter(parent);
        try
        {
            EnsureUsable();
            var answer = await OwnFrameAsync(call, cancellationToken).ConfigureAwait(false);
            EnsureParentRunning(parent);

            return answer;
        }
        finally
        {
            Leave(parent);
        }
    }

    /// <summary>
    /// Commits the unit — or, when it was poisoned or a call is still running, rolls it back and throws why.
    /// </summary>
    /// <remarks>Takes no token: once decided, the commit runs to its end (Ruling 50).</remarks>
    internal async Task CommitAsync()
    {
        var (failure, holdsGate) = Decide();
        if (!holdsGate)
        {
            await CancelCallsAsync().ConfigureAwait(false);
            await _calls.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            if (failure is not null)
            {
                await EndAsync(commit: false).ConfigureAwait(false);
                ExceptionDispatchInfo.Throw(failure);
            }

            await EndAsync(commit: true).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    /// <summary>
    /// The failure the unit recorded, when <paramref name="thrown"/> is the refusal a call on the failed unit threw for it —
    /// so a delegate that caught the first failure and made one more call still surfaces that failure, not its wrapper.
    /// </summary>
    /// <param name="thrown">What the delegate threw.</param>
    internal Exception? RecordedFailureBehind(Exception thrown)
    {
        lock (_gate)
        {
            return thrown is InvalidOperationException { InnerException: { } inner } && ReferenceEquals(inner, _failure) ? inner : null;
        }
    }

    /// <summary>The unit's timeout, as the exception every caller of a timed-out unit sees.</summary>
    /// <param name="cause">What the delegate or the call failed with at the deadline, if anything.</param>
    internal TimeoutException TimedOutBecause(Exception? cause) => new(
        $"The transaction unit ran longer than its Timeout of {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so it "
        + "was cancelled and rolled back; nothing it wrote was committed. Fix: keep the work inside TransactAsync short — make any "
        + "network call before it — or raise AlvoTransactionOptions.Timeout.",
        cause);

    /// <summary>Releases the unit: rolls back what it did not commit, ends its scope, and takes it off the flow.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
        }

        try
        {
            await _onDeadline.DisposeAsync().ConfigureAwait(false);
            if (CallInFlight())
            {
                await CancelCallsAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await RollBackWhenFreeAsync().ConfigureAwait(false);
            }
            finally
            {
                await DisposeScopeAndClocksAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a call holds the unit's gate now; a racy read, so a missed cancel only falls back to the deadline.</summary>
    private bool CallInFlight()
    {
        lock (_gate)
        {
            return _calls.CurrentCount == 0;
        }
    }

    /// <summary>Cancels the unit's calls; a host callback registered on <see cref="Token"/> that throws is ignored.</summary>
    private async Task CancelCallsAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            return;
        }
    }

    /// <summary>Takes the gate a call runs under — the unit's, or its parent call's — or refuses the call and poisons the unit.</summary>
    private void Enter(CallFrame? parent)
    {
        var admission = parent?.AdmitChild() ?? (_calls.Wait(0, CancellationToken.None) ? Admission.Admitted : Admission.Busy);
        if (admission == Admission.Admitted)
        {
            return;
        }

        EnsureUsable();
        Refuse(admission == Admission.Busy ? Concurrent() : Outlived());
    }

    private void Leave(CallFrame? parent)
    {
        if (parent is null)
        {
            _calls.Release();
        }
        else
        {
            parent.ChildLeft();
        }
    }

    /// <summary>Runs <paramref name="call"/> in a frame of its own, and finishes that frame when it returns (Ruling 54).</summary>
    private async Task<T> OwnFrameAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var own = new CallFrame(this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _frame.Value = own;
        try
        {
            return await GuardedAsync(call, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (own.Finish())
            {
                Poison(Outlived());
            }
        }
    }

    /// <summary>Refuses the answer of a nested call that ran on after the call that started it returned (Ruling 54).</summary>
    private void EnsureParentRunning(CallFrame? parent)
    {
        if (parent is { IsFinished: true })
        {
            Refuse(Outlived());
        }
    }

    [DoesNotReturn]
    private void Refuse(InvalidOperationException refusal)
    {
        Poison(refusal);
        throw refusal;
    }

    private async Task<T> GuardedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken token)
    {
        try
        {
            return await call(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled) when (TimedOut)
        {
            throw TimedOutBecause(cancelled);
        }
        catch (Exception failure) when (failure is OperationCanceledException or AlvoChainDepthExceededException)
        {
            Poison(failure);
            throw;
        }
    }

    private void EnsureUsable()
    {
        lock (_gate)
        {
            if (_failure is TimeoutException)
            {
                throw TimedOutBecause(null);
            }

            if (_failure is { } failure)
            {
                throw Failed(failure);
            }

            if (_ended || _released)
            {
                throw Ended();
            }
        }
    }

    /// <summary>
    /// The commit decision, under one lock: whether the gate is free, and what the unit fails with — its poison, the end it
    /// already reached, the deadline, a call still running, or the caller's cancellation. From here on no call is admitted
    /// and no poison is recorded.
    /// </summary>
    /// <remarks>
    /// A deadline that has fired wins over the commit even when its callback has not yet recorded the timeout — the timer
    /// thread can lose the race for the lock to a returning delegate (Ruling 51).
    /// </remarks>
    private (Exception? Failure, bool HoldsGate) Decide()
    {
        lock (_gate)
        {
            var holdsGate = _calls.Wait(0, CancellationToken.None);
            if (_ended || _released)
            {
                return (_failure ?? Ended(), holdsGate);
            }

            _ended = true;
            _failure ??= TimedOut ? TimedOutBecause(null) : holdsGate ? CancelledBeforeTheDecision() : Unawaited();

            return (_failure, holdsGate);
        }
    }

    /// <summary>The caller's cancellation, when it landed before the decision — even if nothing observed it (Ruling 50).</summary>
    private OperationCanceledException? CancelledBeforeTheDecision() =>
        _lifetime.IsCancellationRequested ? new OperationCanceledException(_lifetime.Token) : null;

    private async Task EndAsync(bool commit)
    {
        lock (_gate)
        {
            if (_ending)
            {
                return;
            }

            _ending = true;
            _ended = true;
        }

        var committed = false;
        try
        {
            await (commit ? _parts.Transaction.CommitAsync(CancellationToken.None) : RollBackAsync()).ConfigureAwait(false);
            committed = commit;
        }
        finally
        {
            await ReleaseAfterAsync(committed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the parts; after a successful <c>COMMIT</c>, a failure doing so is logged, not thrown (Ruling 47) — the
    /// write turn is released either way.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Only after a successful COMMIT: the data is committed, so any cleanup failure must not read as the unit's failure.")]
    private async Task ReleaseAfterAsync(bool committed)
    {
        try
        {
            await ReleasePartsAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (committed)
        {
            CleanupFailedAfterCommit(Logger(), failure);
        }
    }

    private ILogger Logger() =>
        _parts.Scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(WriteUnit).FullName!) ?? NullLogger.Instance;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A transaction unit committed, but releasing its transaction or context then failed. The data is committed "
            + "and the write turn released; the unit reports success, so do not retry it.")]
    private static partial void CleanupFailedAfterCommit(ILogger logger, Exception failure);

    /// <summary>Lets go of the transaction, the context and then the write turn — each whatever the one before it threw.</summary>
    private async Task ReleasePartsAsync()
    {
        try
        {
            await _parts.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _parts.Root.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseTurn();
            }
        }
    }

    private void ReleaseTurn()
    {
        try
        {
            _parts.Turn.Dispose();
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
            }
        }
    }

    private async Task RollBackWhenFreeAsync()
    {
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndAsync(commit: false).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    private async Task DisposeScopeAndClocksAsync()
    {
        try
        {
            await _parts.Scope.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            _deadline.Dispose();
        }
    }

    private async Task RollBackAsync()
    {
        try
        {
            await _parts.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private void OnDeadline()
    {
        Poison(TimedOutBecause(null));
        _ = AbandonAsync();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Runs unobserved off the deadline's timer; the unit's failure is already recorded and surfaces from TransactAsync.")]
    private async Task AbandonAsync()
    {
        try
        {
            await RollBackWhenFreeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
    }

    private static InvalidOperationException Failed(Exception failure) => new(
        $"This transaction unit has already failed with {failure.GetType().Name}, so it will roll back and accepts no more calls. "
        + "Fix: let the failure end the delegate, or make the call in a new TransactAsync.",
        failure);

    private static InvalidOperationException Ended() => new(
        "This transaction unit has ended: its TransactAsync has returned, so it committed or rolled back. Fix: make every call "
        + "through the IAlvoTransaction inside the delegate, and await it there.");

    private static InvalidOperationException Unawaited() => new(
        "A call on this transaction was still running when the TransactAsync delegate returned: it was started and not "
        + "awaited, so the unit cannot commit without it or with it. The unit rolled back; nothing it wrote was committed. "
        + "Fix: await every call inside the delegate before it returns.");

    private static InvalidOperationException Outlived() => new(
        "A call on this transaction ran after the call that started it returned: it was started inside that call and not "
        + "awaited there, so it would run beside the unit's other calls or its COMMIT. The unit is now failed and will roll back. "
        + "Fix: await every call made inside a call before that call returns.");

    private static InvalidOperationException Concurrent() => new(
        "Another call on this transaction is still running. A unit runs one call at a time, on one connection — it is now "
        + "failed and will roll back. Fix: await each call before making the next.");

    private enum Admission
    {
        Admitted,
        Busy,
        Finished,
    }

    /// <summary>
    /// One running call of a unit, as the calls it makes in turn see it: they run one at a time under it, and only while it
    /// runs (Ruling 54).
    /// </summary>
    /// <remarks>
    /// Set in <see cref="AsyncLocal{T}"/> inside <see cref="RunAsync{T}"/>, so it is visible to the call's own flow and to
    /// nothing the delegate does beside it; the async method's return restores the caller's frame. A task the call starts
    /// carries the frame past the call's return — which is why the frame is finished then, not merely dropped.
    /// </remarks>
    /// <param name="unit">The unit the call belongs to.</param>
    private sealed class CallFrame(WriteUnit unit)
    {
        private readonly Lock _gate = new();
        private bool _childRunning;
        private bool _finished;

        internal WriteUnit Unit { get; } = unit;

        internal bool IsFinished
        {
            get
            {
                lock (_gate)
                {
                    return _finished;
                }
            }
        }

        internal Admission AdmitChild()
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return Admission.Finished;
                }

                if (_childRunning)
                {
                    return Admission.Busy;
                }

                _childRunning = true;
                return Admission.Admitted;
            }
        }

        internal void ChildLeft()
        {
            lock (_gate)
            {
                _childRunning = false;
            }
        }

        /// <summary>Finishes the frame; answers whether a call it started is still running.</summary>
        internal bool Finish()
        {
            lock (_gate)
            {
                _finished = true;
                return _childRunning;
            }
        }
    }
}
