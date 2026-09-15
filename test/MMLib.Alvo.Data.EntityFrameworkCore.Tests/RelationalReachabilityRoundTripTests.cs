using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The round trip itself: that the probe really makes one, and that a connection which opens but cannot
/// answer is unreachable.
/// </summary>
/// <remarks>
/// <see cref="RelationalReachabilityTests"/> scripts the <em>open</em>, which is where the classification
/// decisions live. What no real engine can be driven into on demand is a socket that accepts a connection
/// and then fails the statement — the exact false "reachable" a probe holding a dead-but-cached connection
/// would report, and the reason this class owns its connection and executes something on it.
/// </remarks>
public class RelationalReachabilityRoundTripTests
{
    /// <summary>
    /// A healthy store answers, and the probe got that answer by executing the dialect's one statement on
    /// the connection it opened — not by concluding "reachable" from a connection object that exists.
    /// </summary>
    [Fact]
    public async Task The_probe_reaches_the_store_by_executing_one_statement_on_it()
    {
        var connection = RoundTripConnection.Answering();

        var reachability = await Probe(connection).ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeTrue();
        connection.Executed.ShouldBe(["SELECT 1"]);
    }

    /// <summary>
    /// The statement touches no table, so it can fail for one reason only: the engine did not answer. That
    /// is unreachability, carrying the provider's own exception as the reason.
    /// </summary>
    [Fact]
    public async Task A_store_that_accepts_the_connection_but_never_answers_is_unreachable()
    {
        var connection = RoundTripConnection.Failing(new SqliteException("database is locked", 5, 5));

        var reachability = await Probe(connection).ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldBeOfType<SqliteException>();
    }

    private static RelationalReachability Probe(DbConnection connection) =>
        new(new RelationalConnectionFactory(() => connection));

    /// <summary>
    /// A connection that opens, records what is executed on it, and either answers or fails. Everything the
    /// probe does not touch throws, so a future edit that reached for one fails loudly instead of silently
    /// measuring a fake.
    /// </summary>
    private sealed class RoundTripConnection : DbConnection
    {
        private readonly Exception? _failure;
        private ConnectionState _state = ConnectionState.Closed;

        private RoundTripConnection(Exception? failure) => _failure = failure;

        /// <summary>A store that answers the round trip.</summary>
        internal static RoundTripConnection Answering() => new(null);

        /// <summary>A store that accepts the connection and then fails the round trip.</summary>
        internal static RoundTripConnection Failing(Exception failure) => new(failure);

        /// <summary>The statements the probe actually executed, in order.</summary>
        internal List<string> Executed { get; } = [];

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbCommand CreateDbCommand() => new RoundTripCommand(this);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        private int Execute(string statement)
        {
            Executed.Add(statement);
            return _failure is null ? 1 : throw _failure;
        }

        private sealed class RoundTripCommand(RoundTripConnection owner) : DbCommand
        {
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            public override int CommandTimeout { get; set; }

            public override CommandType CommandType { get; set; }

            public override bool DesignTimeVisible { get; set; }

            public override UpdateRowSource UpdatedRowSource { get; set; }

            protected override DbConnection? DbConnection { get; set; } = owner;

            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel() => throw new NotSupportedException();

            public override int ExecuteNonQuery() => throw new NotSupportedException();

            public override object? ExecuteScalar() => owner.Execute(CommandText);

            public override void Prepare() => throw new NotSupportedException();

            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                throw new NotSupportedException();
        }
    }
}
