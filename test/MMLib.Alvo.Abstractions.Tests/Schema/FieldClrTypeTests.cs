using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Abstractions.Tests.Schema;

/// <summary>
/// The framework's single <see cref="FieldType"/> → CLR type authority, tested directly.
/// </summary>
/// <remarks>
/// It had none of its own coverage when it was extracted: the EF package's <c>FieldClrTypeMap</c> tests
/// exercised it transitively, and the HTTP payload facts exercised it through a request. Neither would say
/// which arm was wrong, and neither covers the whole table — this is the contract <c>IAlvoData</c>
/// publishes to callers ("a <c>uuid</c> field reads back as a <see cref="Guid"/>"), so every arm is spelled
/// out here rather than derived from the code under test.
/// </remarks>
public class FieldClrTypeTests
{
    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid))]
    [InlineData(FieldType.Ref, typeof(Guid))]
    [InlineData(FieldType.String, typeof(string))]
    [InlineData(FieldType.Text, typeof(string))]
    [InlineData(FieldType.Json, typeof(string))]
    [InlineData(FieldType.Enum, typeof(string))]
    [InlineData(FieldType.Integer, typeof(long))]
    [InlineData(FieldType.Decimal, typeof(decimal))]
    [InlineData(FieldType.Boolean, typeof(bool))]
    [InlineData(FieldType.Date, typeof(DateOnly))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset))]
    public void Every_declared_field_type_maps_to_the_clr_type_the_port_publishes(FieldType type, Type expected)
        => FieldClrType.Of(type).ShouldBe(expected);

    /// <summary>
    /// Every value of the enum is covered above. Derived from the enum rather than counted by hand, so a new
    /// <see cref="FieldType"/> added without a mapping fails here instead of surfacing as a 500 on the first
    /// request that touches it.
    /// </summary>
    [Fact]
    public void No_declared_field_type_is_left_unmapped()
    {
        var unmapped = Enum.GetValues<FieldType>()
            .Where(type => !Mapped(type))
            .ToList();

        unmapped.ShouldBeEmpty($"these field types have no CLR mapping: {string.Join(", ", unmapped)}");
    }

    /// <summary>
    /// <b>Nullability is deliberately not modelled here.</b> Whether a column is <c>Guid</c> or <c>Guid?</c>
    /// is a question about one model, so the wrapping stays with whoever builds that model
    /// (<c>FieldClrTypeMap.Exact</c>/<c>Optional</c> in the EF package). A declared-nullable field maps to
    /// the same bare type as a required one.
    /// </summary>
    [Fact]
    public void A_nullable_field_maps_to_the_same_bare_type_as_a_required_one()
    {
        var nullable = new FieldSchema { Name = "when", Type = FieldType.DateTime, Nullable = true };
        var required = new FieldSchema { Name = "when", Type = FieldType.DateTime, Required = true };

        FieldClrType.Of(nullable).ShouldBe(typeof(DateTimeOffset));
        FieldClrType.Of(required).ShouldBe(typeof(DateTimeOffset));
    }

    /// <summary>
    /// A field type this build does not know throws rather than guessing. That is the arm the HTTP layer's
    /// binder must let propagate as a broken invariant (a 500), never launder into a client error — an
    /// unmapped field type is a fault of whoever composed the schema, not of the caller.
    /// </summary>
    [Fact]
    public void An_unmapped_field_type_is_refused_rather_than_guessed()
    {
        var exception = Should.Throw<NotSupportedException>(() => FieldClrType.Of((FieldType)999));

        exception.Message.ShouldContain("999");
    }

    [Fact]
    public void A_null_field_is_refused()
        => Should.Throw<ArgumentNullException>(() => FieldClrType.Of((FieldSchema)null!));

    private static bool Mapped(FieldType type)
    {
        try
        {
            FieldClrType.Of(type);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
