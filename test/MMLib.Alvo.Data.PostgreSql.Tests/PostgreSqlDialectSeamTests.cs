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

        await Dialect.ExclusionPrerequisiteAsync(connection, [], Ct);

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
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(new ProbeConnection(installed, available, mayInstall), [], Ct);

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
                var refusal = prerequisite.Refusals.ShouldHaveSingleItem();
                refusal.Pointer.ShouldBeNull();
                refusal.Message.ShouldBe(
                    "Excluding overlaps needs PostgreSQL's btree_gist extension, which this database does not have and this connection's role may not install. Nothing was applied.");
                refusal.Fix.ShouldBe(
                    "Ask the database owner to run CREATE EXTENSION btree_gist; in this database (it ships with PostgreSQL and is trusted since PostgreSQL 13), then apply again.");
                break;
        }
    }

    [Fact]
    public async Task A_prerequisite_probe_without_a_connection_is_refused()
        => (await Should.ThrowAsync<ArgumentNullException>(async () => await Dialect.ExclusionPrerequisiteAsync(null!, [], Ct)))
            .ParamName.ShouldBe("connection");

    [Fact]
    public async Task A_prerequisite_probe_without_equality_fields_is_refused()
        => (await Should.ThrowAsync<ArgumentNullException>(async () => await Dialect.ExclusionPrerequisiteAsync(new ProbeConnection(true, true, true), null!, Ct)))
            .ParamName.ShouldBe("equalityFields");

    [Fact]
    public async Task The_prerequisite_probe_asks_which_equality_types_btree_gist_can_compare()
    {
        var connection = new ProbeConnection(true, true, true);

        await Dialect.ExclusionPrerequisiteAsync(connection, [], Ct);

        connection.CommandText.ShouldContain("pg_opclass");
        connection.CommandText.ShouldContain("amname = 'gist'");
        connection.CommandText.ShouldContain("opcdefault");
        connection.CommandText.ShouldContain("'boolean'");
    }

    /// <summary>
    /// Issue 477: btree_gist compares a boolean from PostgreSQL 15 (btree_gist 1.7). Before the extension is installed, the
    /// version the server ships decides; the field is refused at its own pointer, naming the minimum version.
    /// </summary>
    [Fact]
    public async Task A_boolean_equality_field_on_a_server_older_than_15_is_refused_at_its_pointer()
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: false, available: true, mayInstall: true, serverVersion: 140011), [Archived], Ct);

        prerequisite.Statement.ShouldBeNull();
        var refusal = prerequisite.Refusals.ShouldHaveSingleItem();
        refusal.Pointer.ShouldBe("/entities/bookings/indexes/0/fields/1");
        refusal.Message.ShouldBe(
            "Excluding overlaps per 'archived' compares a boolean for equality, which btree_gist cannot do on this PostgreSQL 14 server. Nothing was applied.");
        refusal.Fix.ShouldBe("Upgrade the server to PostgreSQL 15 or later, or remove 'archived' from 'fields'.");
    }

    [Fact]
    public async Task A_boolean_equality_field_on_15_installs_btree_gist_as_usual()
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: false, available: true, mayInstall: true, serverVersion: 150000), [Archived], Ct);

        prerequisite.Statement.ShouldBe("CREATE EXTENSION IF NOT EXISTS btree_gist");
        prerequisite.Refused.ShouldBeFalse();
    }

    /// <summary>
    /// Once installed, the extension's own operator classes decide — a server upgraded with pg_upgrade keeps the old
    /// extension version until it is updated, so a version-15 server can still lack the boolean class.
    /// </summary>
    [Fact]
    public async Task An_installed_btree_gist_without_the_boolean_class_on_15_is_refused_with_the_update()
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: true, available: true, mayInstall: false, serverVersion: 150004, uncomparable: "boolean"), [Archived], Ct);

        var refusal = prerequisite.Refusals.ShouldHaveSingleItem();
        refusal.Pointer.ShouldBe("/entities/bookings/indexes/0/fields/1");
        refusal.Message.ShouldBe(
            "Excluding overlaps per 'archived' compares a boolean for equality, which the btree_gist installed in this database cannot do. Nothing was applied.");
        refusal.Fix.ShouldBe("Ask the database owner to run ALTER EXTENSION btree_gist UPDATE; in this database, then apply again.");
    }

    [Fact]
    public async Task An_installed_btree_gist_with_the_boolean_class_is_ready_whatever_the_version_number_says()
        => (await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: true, available: true, mayInstall: false, serverVersion: 140000), [Archived], Ct))
            .ShouldBe(ExclusionPrerequisite.Ready);

    [Fact]
    public async Task An_installed_btree_gist_without_the_boolean_class_on_14_names_the_upgrade()
        => (await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: true, available: true, mayInstall: false, serverVersion: 140000, uncomparable: "boolean"), [Archived], Ct))
            .Refusals.ShouldHaveSingleItem().Fix.ShouldBe("Upgrade the server to PostgreSQL 15 or later, or remove 'archived' from 'fields'.");

    /// <summary>Every other admitted equality type is compared by every supported server's btree_gist (probed 12 to 16).</summary>
    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Enum)]
    [InlineData(FieldType.Integer)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Uuid)]
    [InlineData(FieldType.Ref)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.DateTime)]
    public async Task Every_other_equality_type_on_12_installs_btree_gist_as_usual(FieldType type)
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: false, available: true, mayInstall: true, serverVersion: 120000),
            [new ExclusionEqualityField("/entities/bookings/indexes/0/fields/0", "room", type)], Ct);

        prerequisite.Statement.ShouldBe("CREATE EXTENSION IF NOT EXISTS btree_gist");
        prerequisite.Refused.ShouldBeFalse();
    }

    /// <summary>A database lacking the extension and a boolean the server cannot compare: both are reported at once.</summary>
    [Fact]
    public async Task A_missing_extension_and_an_uncomparable_field_are_both_refused()
    {
        var prerequisite = await Dialect.ExclusionPrerequisiteAsync(
            new ProbeConnection(installed: false, available: false, mayInstall: false, serverVersion: 140000), [Archived], Ct);

        prerequisite.Refusals.Select(refusal => refusal.Pointer).ShouldBe([null, "/entities/bookings/indexes/0/fields/1"]);
    }

    private static ExclusionEqualityField Archived => new("/entities/bookings/indexes/0/fields/1", "archived", FieldType.Boolean);

    private static PostgresException Failure(string sqlState) =>
        new(messageText: "refused", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    /// <summary>
    /// A connection whose one command answers the probe's single row: three booleans, the server's version number, and the
    /// comma-separated types the installed btree_gist cannot compare.
    /// </summary>
    private sealed class ProbeConnection(bool installed, bool available, bool mayInstall, int serverVersion = 160000, string uncomparable = "") : DbConnection
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

        protected override DbCommand CreateDbCommand() => new ProbeCommand(this, installed, available, mayInstall, serverVersion, uncomparable);

        internal void Record(string text) => CommandText = text;
    }

    private sealed class ProbeCommand(ProbeConnection owner, bool installed, bool available, bool mayInstall, int serverVersion, string uncomparable) : DbCommand
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
            table.Columns.Add("server_version_num", typeof(int));
            table.Columns.Add("uncomparable", typeof(string));
            table.Rows.Add(installed, available, mayInstall, serverVersion, uncomparable);
            return table.CreateDataReader();
        }
    }
}
