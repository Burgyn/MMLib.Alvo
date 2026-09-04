namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class RecordMaterializerTests
{
    [Fact]
    public void Every_projected_column_becomes_a_field_with_its_clr_value()
    {
        var id = Guid.NewGuid();
        var record = RecordMaterializer.ToRecord(Row(("id", id), ("mileage", 42L)), Hidden(), Unselected());

        record["id"].ShouldBe(id);
        record["mileage"].ShouldBe(42L);
    }

    /// <summary>
    /// The masked column arrives as a projected SQL <c>NULL</c>; the key is dropped as well, so a caller
    /// cannot tell a masked field from one the entity does not declare.
    /// </summary>
    [Fact]
    public void A_masked_field_is_absent_rather_than_present_and_null()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("secret", null)), Hidden("secret"), Unselected());

        record.Values.ContainsKey("secret").ShouldBeFalse();
    }

    /// <summary>
    /// A masked column whose value somehow reached this process anyway — a dialect that ignored the null
    /// projection, a row from a source that never applied one — still has its key dropped. Masking on the
    /// way out is the second of the two independent gates, not a formality over an already-null value.
    /// </summary>
    [Fact]
    public void A_masked_field_carrying_a_real_value_is_still_dropped()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("secret", "shh")), Hidden("secret"), Unselected());

        record.Values.ContainsKey("secret").ShouldBeFalse();
    }

    [Fact]
    public void A_genuinely_null_visible_field_stays_present()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("status", null)), Hidden(), Unselected());

        record.Values.ContainsKey("status").ShouldBeTrue();
        record["status"].ShouldBeNull();
    }

    /// <summary>
    /// An unselected field is dropped the same way a masked one is — the observable rule
    /// <see cref="MMLib.Alvo.Data.AlvoQuery.Select"/> promises is that the key is <b>absent</b>, not
    /// present and null.
    /// </summary>
    [Fact]
    public void An_unselected_field_is_absent_rather_than_present_and_null()
    {
        var record = RecordMaterializer.ToRecord(
            Row(("id", Guid.NewGuid()), ("notes", null)), Hidden(), Unselected("notes"));

        record.Values.ContainsKey("notes").ShouldBeFalse();
    }

    /// <summary>
    /// Same as the masked case's second gate: an unselected column whose value reached this process anyway
    /// still has its key dropped, so the record is narrowed whether or not the statement was.
    /// </summary>
    [Fact]
    public void An_unselected_field_carrying_a_real_value_is_still_dropped()
    {
        var record = RecordMaterializer.ToRecord(
            Row(("id", Guid.NewGuid()), ("notes", "kept in the row")), Hidden(), Unselected("notes"));

        record.Values.ContainsKey("notes").ShouldBeFalse();
    }

    /// <summary>
    /// The two sets overlap on every projected read of a masked entity, and either alone is enough to drop
    /// the key. Pinned so a later refactor cannot make one of them the only gate.
    /// </summary>
    [Fact]
    public void A_field_in_both_sets_is_dropped_once_and_without_complaint()
    {
        var record = RecordMaterializer.ToRecord(
            Row(("id", Guid.NewGuid()), ("secret", null)), Hidden("secret"), Unselected("secret"));

        record.Values.ContainsKey("secret").ShouldBeFalse();
        record.Values.ContainsKey("id").ShouldBeTrue();
    }

    [Fact]
    public void Every_argument_is_required()
    {
        Should.Throw<ArgumentNullException>(() => RecordMaterializer.ToRecord(null!, Hidden(), Unselected()));
        Should.Throw<ArgumentNullException>(() => RecordMaterializer.ToRecord(Row(), null!, Unselected()));
        Should.Throw<ArgumentNullException>(() => RecordMaterializer.ToRecord(Row(), Hidden(), null!));
    }

    private static Dictionary<string, object> Row(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value!, StringComparer.Ordinal);

    private static HashSet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Unselected(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
