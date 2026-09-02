using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The four branches of <see cref="RelationalReachability"/> that no real engine can be driven into on
/// demand: which failures are an <em>answer</em>, which propagate, and what a failure raised after the
/// caller's bound elapsed is reported as.
/// </summary>
/// <remarks>
/// <para>
/// The reachable and unreachable paths are pinned against real engines by
/// <see cref="MMLib.Alvo.Testing.Data.AlvoDataReachabilityContractTests"/>, which both drivers inherit. What
/// that suite cannot reach is the <em>classification</em>: a real store cannot be made to raise a
/// <see cref="TimeoutException"/> on command, nor to raise a <see cref="DbException"/> at the exact moment a
/// token is cancelled, and both decisions are one line each.
/// </para>
/// <para>
/// <b>The cancellation branch is the one worth the fake.</b> Its whole job is to stop a probe that was merely
/// too slow from being reported as a database outage, and it is a single
/// <c>cancellationToken.ThrowIfCancellationRequested()</c> — the textbook shape a mutation run deletes and
/// nothing notices.
/// </para>
/// </remarks>
public class RelationalReachabilityTests
{
    /// <summary>The engine refusing to answer is an answer, carrying the reason.</summary>
    [Fact]
    public async Task A_provider_exception_is_answered_as_unreachable()
    {
        var reachability = await Probe(ScriptedConnection.ThrowingOnOpen(SqliteFailure())).ProbeAsync(
            TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldBeOfType<SqliteException>();
    }

    /// <summary>
    /// So is a timeout, which is not a <see cref="DbException"/> at all on every driver — the second arm of
    /// the classification, and unreachable by any real store on demand.
    /// </summary>
    [Fact]
    public async Task A_timeout_is_answered_as_unreachable()
    {
        var reachability = await Probe(ScriptedConnection.ThrowingOnOpen(new TimeoutException("no answer")))
            .ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldBeOfType<TimeoutException>();
    }

    /// <summary>
    /// Anything else <b>propagates</b>. A misconfiguration is not unreachability, and reporting it as one
    /// would drain a pod's traffic for a defect no restart of the database can fix — while the health-check
    /// service already reports a check that threw as this registration's failure status, with its own log
    /// record.
    /// </summary>
    [Fact]
    public async Task An_unexpected_failure_propagates_rather_than_being_reported_as_unreachable()
    {
        var probe = Probe(ScriptedConnection.ThrowingOnOpen(new InvalidOperationException("a broken invariant")));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await probe.ProbeAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A provider exception raised <em>after</em> the caller's bound elapsed is reported as the cancellation it
    /// is, never as an unreachable store.
    /// </summary>
    /// <remarks>
    /// This is the branch a real engine cannot produce on demand, and the reason it matters is a wrong page: a
    /// readiness probe whose two-second bound elapsed would otherwise be logged at <c>Error</c> as "Alvo cannot
    /// reach its store", sending an operator to a database that is perfectly healthy and merely slow to
    /// answer. Drivers really do surface cancellation as their own exception type, which is why the check is on
    /// the token rather than on the exception.
    /// </remarks>
    [Fact]
    public async Task A_provider_exception_raised_after_the_bound_elapsed_is_reported_as_cancellation()
    {
        using var elapsed = new CancellationTokenSource();
        var probe = Probe(ScriptedConnection.CancellingThenThrowing(elapsed, SqliteFailure()));

        await Should.ThrowAsync<OperationCanceledException>(async () => await probe.ProbeAsync(elapsed.Token));
    }

    private static RelationalReachability Probe(DbConnection connection) =>
        new(new RelationalConnectionFactory(() => connection));

    /// <summary>A real provider exception, so the answered path is asserted against a genuine one.</summary>
    private static SqliteException SqliteFailure() => new("unable to open database file", 14, 14);

    /// <summary>
    /// A connection that does exactly one scripted thing when it is opened, and refuses everything else.
    /// </summary>
    /// <remarks>
    /// Only <c>Open</c> is scripted, because that is where every failure this class classifies really arrives
    /// — and a fake that also scripted the command would be a second mechanism for one claim. Every other
    /// member throws <see cref="NotSupportedException"/>, so a future edit that reached for one fails loudly
    /// instead of silently measuring a fake.
    /// </remarks>
    private sealed class ScriptedConnection : DbConnection
    {
        private readonly Action _onOpen;

        private ScriptedConnection(Action onOpen) => _onOpen = onOpen;

        internal static ScriptedConnection ThrowingOnOpen(Exception failure) => new(() => throw failure);

        /// <summary>Cancels <paramref name="bound"/> and then fails, in that order.</summary>
        /// <param name="bound">The caller's bound, cancelled as the connection is opened.</param>
        /// <param name="failure">The provider exception raised after it elapsed.</param>
        internal static ScriptedConnection CancellingThenThrowing(
            CancellationTokenSource bound, Exception failure) =>
            new(() =>
            {
                bound.Cancel();
                throw failure;
            });

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => _onOpen();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
    }
}
