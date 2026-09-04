using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class ReadProjectionTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "accounts",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "secret", Type = FieldType.String, Required = true },
            new FieldSchema { Name = "balance", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "visits", Type = FieldType.Integer, Nullable = true },
        ],
    };

    private static readonly IAlvoSqlDialect _dialect = new TestSqlDialect();

    [Fact]
    public void Every_mapped_field_appears_in_the_select_list()
        => Compose(Hidden()).ShouldBe("\"id\", \"title\", \"secret\", \"balance\", \"visits\"");

    /// <summary>
    /// EF refuses a <c>FromSql</c> result set that is missing a mapped property, so a masked field is
    /// still named — as a typed SQL <c>NULL</c>, which is what keeps its stored value inside the table.
    /// </summary>
    [Fact]
    public void A_hidden_field_is_projected_as_a_typed_null_under_its_own_alias()
        => Compose(Hidden("secret"))
            .ShouldBe("\"id\", \"title\", CAST(NULL AS TEXT) AS \"secret\", \"balance\", \"visits\"");

    /// <summary>
    /// Two masked fields whose store types differ, so the cast cannot be one hardcoded type name: the type
    /// is the one EF resolved for that column, per column.
    /// </summary>
    [Fact]
    public void Several_hidden_fields_are_all_masked_and_field_order_is_preserved()
        => Compose(Hidden("secret", "visits")).ShouldBe(
            "\"id\", \"title\", CAST(NULL AS TEXT) AS \"secret\", \"balance\", CAST(NULL AS INTEGER) AS \"visits\"");

    /// <summary>
    /// A masked field's <c>NOT NULL</c>-ness is irrelevant — the read model makes every property optional
    /// precisely so this projection is legal — and the mask does not disturb the visible columns.
    /// </summary>
    [Fact]
    public void Masking_a_not_null_column_still_projects_a_null_for_it()
        => Compose(Hidden("secret")).ShouldContain("CAST(NULL AS TEXT) AS \"secret\"");

    /// <summary>
    /// The fail-closed belt. A masked row key is refused at <em>apply</em> time
    /// (<c>PolicyCatalogBuilder.CompileFieldFlags</c>), so reaching here means the mask arrived from a
    /// schema source that never ran that check — F7's dynamic registry being the obvious next one. EF
    /// re-marks a key property required whatever <c>IsRequired(false)</c> asked, so a projected
    /// <c>NULL</c> for the key throws at materialization with a different exception type per engine;
    /// refusing here turns that into one deterministic denial.
    /// </summary>
    [Fact]
    public void The_row_key_is_never_masked_however_the_mask_arrived()
        => Should.Throw<AlvoAuthorizationException>(() => Compose(Hidden("id")));

    /// <summary>
    /// A masked field the read model does not map has no store type to cast to, and guessing one is how a
    /// second store-type authority gets invented. Refused instead — the same fail-closed rule
    /// <c>QueryFieldGuard</c> applies to an entity the applied schema does not know.
    /// </summary>
    [Fact]
    public void A_masked_field_the_read_model_does_not_map_is_refused_rather_than_guessed()
    {
        var declared = _entity with { Fields = [.. _entity.Fields, new FieldSchema { Name = "ghost", Type = FieldType.String }] };

        Should.Throw<AlvoAuthorizationException>(
            () => ReadProjection.Compose(
                declared, Hidden("ghost"), Unselected(), _dialect, ReadModelFixture.Rows(_entity)));
    }

    /// <summary>
    /// An <em>unselected</em> field the read model does not map is a different failure from a masked one,
    /// and the exception type is the whole point: the caller's own projection must never produce a 403. This
    /// state is unreachable in production — the set is derived from the applied schema's own fields — so
    /// reaching it means the schema and the read model disagree, which is an Alvo defect.
    /// </summary>
    [Fact]
    public void An_unselected_field_the_read_model_does_not_map_is_a_bug_rather_than_a_denial()
    {
        var declared = _entity with { Fields = [.. _entity.Fields, new FieldSchema { Name = "ghost", Type = FieldType.String }] };

        Should.Throw<InvalidOperationException>(
            () => ReadProjection.Compose(
                declared, Hidden(), Unselected("ghost"), _dialect, ReadModelFixture.Rows(_entity)));
    }

    /// <summary>
    /// The two sets overlap on every projected read of a masked entity — a hidden field is never selected,
    /// never a sort key and never framework-managed — so a field in both must answer with the <em>mask's</em>
    /// exception. Pinned because an implementation that tested the unselected set first would pass every
    /// other fact here and silently downgrade a security condition to a bug report.
    /// </summary>
    [Fact]
    public void A_field_in_both_sets_fails_as_a_mask_rather_than_as_a_projection()
    {
        var declared = _entity with { Fields = [.. _entity.Fields, new FieldSchema { Name = "ghost", Type = FieldType.String }] };

        Should.Throw<AlvoAuthorizationException>(
            () => ReadProjection.Compose(
                declared, Hidden("ghost"), Unselected("ghost"), _dialect, ReadModelFixture.Rows(_entity)));
    }

    [Fact]
    public void An_unselected_field_is_projected_as_a_typed_null_under_its_own_name()
    {
        var sql = Compose(Hidden(), Unselected("balance"));

        sql.ShouldContain("AS \"balance\"", Case.Sensitive);
        sql.ShouldContain("\"title\"", Case.Sensitive, "a selected field is still read from its column");
    }

    /// <summary>
    /// The mask and the projection render identically — one <c>NULL</c> cast per excluded column — so a
    /// reader cannot tell from the statement which of the two excluded a field. That is a property, not an
    /// accident: the statement is not the place either decision is recorded.
    /// </summary>
    [Fact]
    public void A_masked_field_and_an_unselected_field_render_the_same_way()
        => Compose(Hidden("balance"), Unselected()).ShouldBe(Compose(Hidden(), Unselected("balance")));

    [Fact]
    public void Every_argument_is_required()
    {
        var rows = ReadModelFixture.Rows(_entity);

        Should.Throw<ArgumentNullException>(
            () => ReadProjection.Compose(null!, Hidden(), Unselected(), _dialect, rows));
        Should.Throw<ArgumentNullException>(
            () => ReadProjection.Compose(_entity, null!, Unselected(), _dialect, rows));
        Should.Throw<ArgumentNullException>(
            () => ReadProjection.Compose(_entity, Hidden(), null!, _dialect, rows));
        Should.Throw<ArgumentNullException>(
            () => ReadProjection.Compose(_entity, Hidden(), Unselected(), null!, rows));
        Should.Throw<ArgumentNullException>(
            () => ReadProjection.Compose(_entity, Hidden(), Unselected(), _dialect, null!));
    }

    private static string Compose(IReadOnlySet<string> hiddenFields) =>
        Compose(hiddenFields, Unselected());

    private static string Compose(IReadOnlySet<string> hiddenFields, IReadOnlySet<string> unselectedFields) =>
        ReadProjection.Compose(
            _entity, hiddenFields, unselectedFields, _dialect, ReadModelFixture.Rows(_entity));

    private static HashSet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Unselected(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
