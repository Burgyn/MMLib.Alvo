using MMLib.Alvo.Data.EntityFrameworkCore;
using Npgsql;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// What this dialect answers when the server refuses a write, and what it refuses to compose.
/// </summary>
/// <remarks>
/// <para>
/// The constraint-name cases exist for the hazard <c>NullIfEmpty</c>'s own doc comment names:
/// <c>PostgresException</c> answers <see cref="string.Empty"/> rather than <see langword="null"/> when the
/// server sent no constraint field, and the shared data path matches that name against the model's own index
/// names — so an empty name would match every index that also has none, and a unique violation would be
/// reported against the wrong index. Both directions are pinned, because only the pair rules that out.
/// </para>
/// <para>
/// A <c>PostgresException</c> is built through its public constructor rather than faked: the type is sealed
/// and its <c>SqlState</c>/<c>ConstraintName</c> come off the error fields, so constructing one is the only
/// way to exercise the decode the way the driver will.
/// </para>
/// </remarks>
public class PostgreSqlConstraintDecodeTests
{
    private static readonly PostgreSqlSqlDialect _dialect = new();

    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";
    private const string StringDataRightTruncation = "22001";

    private static PostgresException Failure(string sqlState, string? constraintName) =>
        new(
            messageText: "refused",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            constraintName: constraintName);

    /// <summary>A named index is reported by name, so the data path can attribute the refusal to it.</summary>
    [Fact]
    public void A_unique_violation_carries_the_constraint_name_the_server_reported()
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure(UniqueViolation, "ix_vehicle_plate"));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.ConstraintName.ShouldBe("ix_vehicle_plate");
    }

    /// <summary>
    /// And an absent one is reported as absent rather than as the empty string — the case the empty name
    /// would otherwise match every unnamed index.
    /// </summary>
    /// <param name="reported">What the server put in the constraint field.</param>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_unique_violation_with_no_constraint_field_carries_no_name(string? reported)
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure(UniqueViolation, reported));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.ConstraintName.ShouldBeNull();
    }

    /// <summary>
    /// A string past its column's <c>varchar(n)</c> (<c>string_data_right_truncation</c>) is the validation family, naming
    /// no constraint (issue 481): the port measures <c>maxLength</c> before the engine, so this is defence in depth for a
    /// value that still reaches it — a stored generated column, say — which was an unmapped 500 before.
    /// </summary>
    [Fact]
    public void A_string_past_its_column_length_is_a_check_violation_naming_no_constraint()
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure(StringDataRightTruncation, "ignored"));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Check);
        decoded.ConstraintName.ShouldBeNull();
    }

    /// <summary>
    /// A foreign-key violation reports the referencing table's constraint, not this row's, so the name is
    /// deliberately dropped and only the kind survives.
    /// </summary>
    [Fact]
    public void A_foreign_key_violation_carries_the_kind_and_no_name()
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure(ForeignKeyViolation, "fk_trip_vehicle"));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        decoded.ConstraintName.ShouldBeNull();
    }

    /// <summary>A field's validation is a CHECK the decode claims (HX-11), carrying its name, or none for an empty field.</summary>
    /// <param name="reported">What the server put in the constraint field.</param>
    /// <param name="expected">The name the decode carries.</param>
    [Theory]
    [InlineData("ck_products_stock_7b86837d", "ck_products_stock_7b86837d")]
    [InlineData("", null)]
    public void A_check_violation_carries_its_constraint_name(string reported, string? expected)
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure(CheckViolation, reported)).ShouldNotBeNull();

        (decoded.Kind, decoded.ConstraintName).ShouldBe((AlvoConstraintKind.Check, expected));
    }

    /// <summary>An exclusion constraint's refusal (<c>23P01</c>) decodes as an exclusion, carrying its name (HX-12).</summary>
    [Fact]
    public void An_exclusion_violation_decodes_its_constraint_name()
    {
        var decoded = _dialect.DecodeConstraintViolation(Failure("23P01", "xc_bookings_6423bd5f")).ShouldNotBeNull();

        (decoded.Kind, decoded.ConstraintName).ShouldBe((AlvoConstraintKind.Exclusion, "xc_bookings_6423bd5f"));
    }

    /// <summary>Anything else is not a constraint violation this driver claims to understand.</summary>
    [Fact]
    public void A_state_this_driver_does_not_decode_answers_nothing()
        => _dialect.DecodeConstraintViolation(Failure("42P01", null)).ShouldBeNull();

    /// <summary>
    /// And a failure that is not a server error is not decoded by guessing at its message.
    /// <see cref="NpgsqlException"/> is this driver's own transport-level failure — a real
    /// <see cref="DbException"/> that carries no <c>SqlState</c> — so it is the honest negative case.
    /// </summary>
    [Fact]
    public void A_failure_that_is_not_a_server_error_answers_nothing()
        => _dialect.DecodeConstraintViolation(new NpgsqlException("the connection dropped")).ShouldBeNull();

    /// <summary>
    /// The column name is guarded like the two arguments beside it. Without the guard a blank name renders a
    /// column definition that is silently wrong rather than refused — and a generated column is DDL, so the
    /// failure would surface as a migration error with no author to attribute it to.
    /// </summary>
    /// <param name="columnName">A name no column can have.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_generated_column_refuses_a_blank_column_name(string columnName)
        => Should.Throw<ArgumentException>(
                () => _dialect.GeneratedColumnDefinition(columnName, "numeric(18,2)", "unit_price * amount"))
            .ParamName.ShouldBe("columnName");

}
