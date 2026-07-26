namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class RecordMaterializerTests
{
    [Fact]
    public void Every_projected_column_becomes_a_field_with_its_clr_value()
    {
        var id = Guid.NewGuid();
        var record = RecordMaterializer.ToRecord(Row(("id", id), ("mileage", 42L)), Hidden());

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
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("secret", null)), Hidden("secret"));

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
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("secret", "shh")), Hidden("secret"));

        record.Values.ContainsKey("secret").ShouldBeFalse();
    }

    [Fact]
    public void A_genuinely_null_visible_field_stays_present()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("status", null)), Hidden());

        record.Values.ContainsKey("status").ShouldBeTrue();
        record["status"].ShouldBeNull();
    }

    private static Dictionary<string, object> Row(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value!, StringComparer.Ordinal);

    private static HashSet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
