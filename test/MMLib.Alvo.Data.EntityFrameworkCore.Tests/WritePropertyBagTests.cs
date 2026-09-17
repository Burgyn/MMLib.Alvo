using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The bag an <c>INSERT</c> is prepared from — the one place both insert paths (the port's own create and
/// the test-only seeding seam) turn a payload into columns.
/// </summary>
public class WritePropertyBagTests
{
    /// <summary>
    /// The read model is the authority for every field's CLR type, so it is refused as
    /// <see langword="null"/> rather than reaching the per-value lookup, where it would surface as a
    /// <see cref="NullReferenceException"/> out of a write.
    /// </summary>
    [Fact]
    public void A_null_read_model_is_refused_and_named() =>
        Should.Throw<ArgumentNullException>(() => WritePropertyBag.For(null!, Payload()))
            .ParamName.ShouldBe("rows");

    [Fact]
    public void A_null_payload_is_refused_and_named() =>
        Should.Throw<ArgumentNullException>(
            () => WritePropertyBag.For(ReadModelFixture.Rows(AlvoDataFixtures.Vehicle), null!))
            .ParamName.ShouldBe("values");

    /// <summary>
    /// A <see langword="null"/> value is dropped rather than written: an absent key already means "leave the
    /// column at its database default", which on an insert is indistinguishable from an explicit
    /// <c>NULL</c> for a nullable column and correct for both.
    /// </summary>
    [Fact]
    public void A_null_value_is_left_out_of_the_bag()
    {
        var bag = WritePropertyBag.For(
            ReadModelFixture.Rows(AlvoDataFixtures.Vehicle),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["plate"] = "ACME-1", ["status"] = null });

        bag.ShouldContainKey("plate");
        bag.ShouldNotContainKey("status");
    }

    /// <summary>
    /// A value is stored as its own column holds it, through the same funnel a filter operand goes through —
    /// so an insert accepts exactly the values a comparison does.
    /// </summary>
    [Fact]
    public void A_value_is_converted_to_the_columns_own_type()
    {
        var bag = WritePropertyBag.For(
            ReadModelFixture.Rows(AlvoDataFixtures.Vehicle),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["mileage"] = "42" });

        bag["mileage"].ShouldBe(42L);
    }

    private static Dictionary<string, object?> Payload() =>
        new(StringComparer.Ordinal) { ["plate"] = "ACME-1" };
}
