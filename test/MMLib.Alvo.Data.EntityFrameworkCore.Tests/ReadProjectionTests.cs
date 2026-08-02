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
            () => ReadProjection.Compose(declared, Hidden("ghost"), _dialect, ReadModelFixture.Rows(_entity)));
    }

    [Fact]
    public void Every_argument_is_required()
    {
        var rows = ReadModelFixture.Rows(_entity);

        Should.Throw<ArgumentNullException>(() => ReadProjection.Compose(null!, Hidden(), _dialect, rows));
        Should.Throw<ArgumentNullException>(() => ReadProjection.Compose(_entity, null!, _dialect, rows));
        Should.Throw<ArgumentNullException>(() => ReadProjection.Compose(_entity, Hidden(), null!, rows));
        Should.Throw<ArgumentNullException>(() => ReadProjection.Compose(_entity, Hidden(), _dialect, null!));
    }

    private static string Compose(IReadOnlySet<string> hiddenFields) =>
        ReadProjection.Compose(_entity, hiddenFields, _dialect, ReadModelFixture.Rows(_entity));

    private static HashSet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
