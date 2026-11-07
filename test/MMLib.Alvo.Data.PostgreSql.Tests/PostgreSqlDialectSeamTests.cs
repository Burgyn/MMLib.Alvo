using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// The members of <see cref="PostgreSqlSqlDialect"/> the migrator, the delivery store and the create path reach through the
/// port, pinned here without a database so the post-merge mutation run, which executes only this project, sees each of
/// them. The Testcontainers suite proves the same texts against a real server; this proves they are the texts.
/// </summary>
public sealed class PostgreSqlDialectSeamTests
{
    private readonly PostgreSqlSqlDialect _concrete = new();

    private IAlvoSqlDialect Dialect => _concrete;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_datetime_exclusion_is_one_gist_constraint_over_a_timestamptz_range_with_its_where()
    {
        var sql = Dialect.ExclusionConstraint(new ExclusionConstraintDefinition(
            "bookings", "xc_bookings_6423bd5f", ["tenant_id", "room"], "starts_at", "ends_at", FieldType.DateTime, "\"status\" <> 'cancelled'")).ShouldNotBeNull();

        sql.Create.ShouldBe(
        [
            "ALTER TABLE \"bookings\" ADD CONSTRAINT \"xc_bookings_6423bd5f\" EXCLUDE USING gist "
            + "(\"tenant_id\" WITH =, \"room\" WITH =, tstzrange(\"starts_at\", \"ends_at\", '[)') WITH &&) WHERE (\"status\" <> 'cancelled')",
        ]);
        sql.Drop.ShouldBe(["ALTER TABLE \"bookings\" DROP CONSTRAINT IF EXISTS \"xc_bookings_6423bd5f\""]);
        sql.SurvivesTableRebuild.ShouldBeTrue();
    }

    [Fact]
    public void A_date_exclusion_without_a_filter_is_a_daterange_with_no_where()
    {
        var sql = Dialect.ExclusionConstraint(new ExclusionConstraintDefinition(
            "stays", "xc_stays", ["room"], "check_in", "check_out", FieldType.Date, null)).ShouldNotBeNull();

        sql.Create.ShouldBe(
        [
            "ALTER TABLE \"stays\" ADD CONSTRAINT \"xc_stays\" EXCLUDE USING gist (\"room\" WITH =, daterange(\"check_in\", \"check_out\", '[)') WITH &&)",
        ]);
    }

    [Fact]
    public void An_exclusion_without_a_definition_is_refused()
        => Should.Throw<ArgumentNullException>(() => Dialect.ExclusionConstraint(null!)).ParamName.ShouldBe("definition");

    [Fact]
    public void A_count_is_allocated_by_the_shared_upsert()
        => Dialect.SequenceAllocationStatement("alvo_sequences").ShouldBe(SequenceTable.Upsert("alvo_sequences"));

    [Fact]
    public void The_apply_bounds_its_lock_waits_to_five_seconds_for_its_own_transaction()
        => Dialect.ApplyLockTimeoutStatement.ShouldBe("SET LOCAL lock_timeout = '5s'");

    [Fact]
    public void Only_the_lock_not_available_state_is_a_lock_timeout()
    {
        Dialect.IsLockTimeout(Failure("55P03")).ShouldBeTrue();
        Dialect.IsLockTimeout(Failure("42P17")).ShouldBeFalse();
        Dialect.IsLockTimeout(new NpgsqlException("the connection dropped")).ShouldBeFalse();
    }

    [Fact]
    public void Only_the_deadlock_detected_state_is_a_deadlock()
    {
        Dialect.IsDeadlock(Failure("40P01")).ShouldBeTrue();
        Dialect.IsDeadlock(Failure("55P03")).ShouldBeFalse();
        Dialect.IsDeadlock(Failure("40001")).ShouldBeFalse();
        Dialect.IsDeadlock(new NpgsqlException("the connection dropped")).ShouldBeFalse();
    }

    [Fact]
    public void Only_the_invalid_object_definition_state_is_an_index_predicate_refusal()
    {
        Dialect.IsIndexPredicateRefusal(Failure("42P17")).ShouldBeTrue();
        Dialect.IsIndexPredicateRefusal(Failure("55P03")).ShouldBeFalse();
        Dialect.IsIndexPredicateRefusal(new NpgsqlException("the connection dropped")).ShouldBeFalse();
    }

    [Fact]
    public void A_backfill_holds_a_share_lock_on_the_child_table()
        => Dialect.BackfillLockStatement(Entity("order_items")).ShouldBe("LOCK TABLE \"order_items\" IN SHARE MODE");

    [Fact]
    public void A_backfill_lock_without_a_child_is_refused()
        => Should.Throw<ArgumentNullException>(() => Dialect.BackfillLockStatement(null!)).ParamName.ShouldBe("child");

    [Fact]
    public void A_transaction_lock_without_keys_is_refused()
        => Should.Throw<ArgumentNullException>(() => _concrete.TransactionLockStatement(null!, exclusive: true)).ParamName.ShouldBe("keyParameterMarkers");

    [Fact]
    public void A_table_without_an_entity_is_refused()
        => Should.Throw<ArgumentNullException>(() => _concrete.RenderTable(null!, lockedPreImageFor: null)).ParamName.ShouldBe("entity");

    [Fact]
    public void A_decode_without_a_failure_is_refused()
        => Should.Throw<ArgumentNullException>(() => _concrete.DecodeConstraintViolation(null!)).ParamName.ShouldBe("failure");

    [Theory]
    [InlineData("", "numeric(18,2)", "x", "columnName")]
    [InlineData("c", "", "x", "storeType")]
    [InlineData("c", " ", "x", "storeType")]
    [InlineData("c", "numeric(18,2)", "", "renderedExpression")]
    [InlineData("c", "numeric(18,2)", " ", "renderedExpression")]
    public void A_generated_column_refuses_each_blank_part_by_name(string column, string storeType, string expression, string parameter)
        => Should.Throw<ArgumentException>(() => _concrete.GeneratedColumnDefinition(column, storeType, expression)).ParamName.ShouldBe(parameter);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_null_projection_refuses_a_blank_store_type(string storeType)
        => Should.Throw<ArgumentException>(() => _concrete.RenderNullProjection(storeType)).ParamName.ShouldBe("storeType");

    [Fact]
    public async Task The_prerequisite_probe_asks_the_server_about_btree_gist_and_the_role()
    {
        var connection = new ProbeConnection(true, true, true);

        await Dialect.ExclusionPrerequisiteAsync(connection, Ct);

        connection.CommandText.ShouldContain("pg_extension");
        connection.CommandText.ShouldContain("pg_available_extensions");
        connection.CommandText.ShouldContain("btree_gist");
        connection.CommandText.ShouldContain("has_schema_privilege");
        connection.CommandText.ShouldContain("server_version_num");
        connection.CommandText.ShouldContain("rolsuper");
    }

    /// <summary>
    /// Installed is ready whatever else is true; not installed needs the extension available <em>and</em> the role able to
    /// install it, and anything else is a refusal naming the fix.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, "ready")]
    [InlineData(true, true, true, "ready")]
    [InlineData(false, true, true, "install")]
    [InlineData(false, true, false, "refused")]
    [InlineData(false, false, true, "refused")]
    [InlineData(false, false, false, "refused")]
    public async Task The_prerequisite_is_decided_from_the_three_answers_of_the_probe(bool installed, bool available, bool mayInstall, string expected)
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(new ProbeConnection(installed, available, mayInstall), Ct);

        switch (expected)
        {
            case "ready":
                prerequisite.ShouldBe(ExclusionPrerequisite.Ready);
                break;
            case "install":
                prerequisite.Statement.ShouldBe("CREATE EXTENSION IF NOT EXISTS btree_gist");
                prerequisite.Refused.ShouldBeFalse();
                break;
            default:
                prerequisite.Statement.ShouldBeNull();
                prerequisite.RefusalMessage.ShouldBe(
                    "Excluding overlaps needs PostgreSQL's btree_gist extension, which this database does not have and this connection's role may not install. Nothing was applied.");
                prerequisite.RefusalFix.ShouldBe(
                    "Ask the database owner to run CREATE EXTENSION btree_gist; in this database (it ships with PostgreSQL and is trusted since PostgreSQL 13), then apply again.");
                break;
        }
    }

    [Fact]
    public async Task A_prerequisite_probe_without_a_connection_is_refused()
        => (await Should.ThrowAsync<ArgumentNullException>(async () => await Dialect.ExclusionPrerequisiteAsync(null!, Ct)))
            .ParamName.ShouldBe("connection");

    private static PostgresException Failure(string sqlState) =>
        new(messageText: "refused", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    /// <summary>A connection whose one command answers the probe's single row of three booleans.</summary>
    private sealed class ProbeConnection(bool installed, bool available, bool mayInstall) : DbConnection
    {
        public string CommandText { get; private set; } = string.Empty;

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "probe";

        public override string DataSource => "probe";

        public override string ServerVersion => "16";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new ProbeCommand(this, installed, available, mayInstall);

        internal void Record(string text) => CommandText = text;
    }

    private sealed class ProbeCommand(ProbeConnection owner, bool installed, bool available, bool mayInstall) : DbCommand
    {
        private string _text = string.Empty;

        [AllowNull]
        public override string CommandText
        {
            get => _text;
            set
            {
                _text = value ?? string.Empty;
                owner.Record(_text);
            }
        }

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            table.Columns.Add("installed", typeof(bool));
            table.Columns.Add("available", typeof(bool));
            table.Columns.Add("may_install", typeof(bool));
            table.Rows.Add(installed, available, mayInstall);
            return table.CreateDataReader();
        }
    }
}
