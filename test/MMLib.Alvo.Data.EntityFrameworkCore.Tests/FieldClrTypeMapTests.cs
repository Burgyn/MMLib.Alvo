using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class FieldClrTypeMapTests
{
    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid))]
    [InlineData(FieldType.Ref, typeof(Guid))]
    [InlineData(FieldType.Integer, typeof(long))]
    [InlineData(FieldType.Decimal, typeof(decimal))]
    [InlineData(FieldType.Boolean, typeof(bool))]
    [InlineData(FieldType.Date, typeof(DateOnly))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset))]
    public void A_non_nullable_value_field_maps_exactly(FieldType type, Type expected)
        => FieldClrTypeMap.Exact(Field(type, nullable: false)).ShouldBe(expected);

    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid?))]
    [InlineData(FieldType.Integer, typeof(long?))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset?))]
    public void A_nullable_value_field_maps_to_the_nullable_type_in_both_models(FieldType type, Type expected)
    {
        FieldClrTypeMap.Exact(Field(type, nullable: true)).ShouldBe(expected);
        FieldClrTypeMap.Optional(Field(type, nullable: true)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid?))]
    [InlineData(FieldType.Integer, typeof(long?))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset?))]
    public void The_read_model_makes_every_value_field_nullable_even_when_the_column_is_not(FieldType type, Type expected)
        => FieldClrTypeMap.Optional(Field(type, nullable: false)).ShouldBe(expected);

    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Json)]
    [InlineData(FieldType.Enum)]
    public void A_string_backed_field_is_a_string_in_both_models(FieldType type)
    {
        FieldClrTypeMap.Exact(Field(type, nullable: false)).ShouldBe(typeof(string));
        FieldClrTypeMap.Optional(Field(type, nullable: false)).ShouldBe(typeof(string));
    }

    /// <summary>
    /// A field type this map has no entry for must fail loudly rather than fall back to a plausible
    /// default: the CLR type chosen here is what EF resolves the column's store type from, so a wrong
    /// guess would produce a column, and a comparison, of the wrong type.
    /// </summary>
    [Fact]
    public void An_unmapped_field_type_is_refused_rather_than_guessed()
    {
        var unmapped = Field((FieldType)(-1), nullable: false);

        Should.Throw<NotSupportedException>(() => FieldClrTypeMap.Exact(unmapped));
        Should.Throw<NotSupportedException>(() => FieldClrTypeMap.Optional(unmapped));
    }

    [Fact]
    public void A_missing_field_is_refused()
    {
        Should.Throw<ArgumentNullException>(() => FieldClrTypeMap.Exact(null!));
        Should.Throw<ArgumentNullException>(() => FieldClrTypeMap.Optional(null!));
    }

    private static FieldSchema Field(FieldType type, bool nullable) =>
        new() { Name = "f", Type = type, Nullable = nullable };
}
