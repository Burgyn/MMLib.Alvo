using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>
/// One stored value, as the grid draws it.
/// </summary>
public class GridCellTests
{
    [Theory]
    [InlineData(17, "17.00")]
    [InlineData(12.9, "12.90")]
    [InlineData(21.6, "21.60")]
    public void A_decimal_keeps_its_declared_scale(double stored, string shown)
        => GridCell.Of(Decimal(scale: 2), (decimal)stored).ShouldBe(new GridCell(CellKind.Number, shown));

    [Fact]
    public void A_decimal_is_invariant_whatever_the_servers_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sk-SK");

            GridCell.Of(Decimal(scale: 1), 1m).Text.ShouldBe("1.0", "D-8: the grid never follows a culture, the server's or the browser's");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_decimal_without_a_scale_is_shown_as_stored()
        => GridCell.Of(Decimal(scale: null), 3.125m).Text.ShouldBe("3.125");

    [Fact]
    public void A_decimal_read_back_as_a_double_is_still_an_amount()
        => GridCell.Of(Decimal(scale: 2), 4.5d).ShouldBe(new GridCell(CellKind.Number, "4.50"));

    [Fact]
    public void An_enum_value_is_a_badge()
        => GridCell.Of(Field(FieldType.Enum), "in_progress").ShouldBe(new GridCell(CellKind.Value, "in_progress"));

    [Theory]
    [InlineData(true, "✓", "yes")]
    [InlineData(false, "—", "no")]
    public void A_boolean_is_a_check_or_a_dash(bool stored, string shown, string meaning)
        => GridCell.Of(Field(FieldType.Boolean), stored).ShouldBe(new GridCell(CellKind.Flag, shown, meaning));

    [Fact]
    public void An_instant_is_minutes_and_a_day_is_a_day()
    {
        GridCell.Of(Field(FieldType.DateTime), new DateTimeOffset(2026, 9, 23, 14, 5, 59, TimeSpan.Zero))
            .ShouldBe(new GridCell(CellKind.Moment, "2026-09-23 14:05"));
        GridCell.Of(Field(FieldType.Date), new DateOnly(2026, 9, 23))
            .ShouldBe(new GridCell(CellKind.Moment, "2026-09-23"));
    }

    [Fact]
    public void An_absent_value_is_a_dash()
    {
        GridCell.Of(Field(FieldType.String), null).Kind.ShouldBe(CellKind.Empty);
        GridCell.Of(Field(FieldType.String), string.Empty).Kind.ShouldBe(CellKind.Empty);
    }

    [Fact]
    public void A_uuid_is_its_first_characters_with_the_whole_of_it_in_the_title()
    {
        var id = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        GridCell.Of(Field(FieldType.Uuid), id)
            .ShouldBe(new GridCell(CellKind.Identifier, "0199a1b2", id.ToString()));
    }

    [Fact]
    public void A_short_value_stays_whole_on_one_line()
    {
        var cell = GridCell.Of(Field(FieldType.String), "SO-2026-0163");

        cell.Short.ShouldBeTrue();
        cell.Title.ShouldBeNull();
    }

    [Fact]
    public void A_long_value_is_clipped_and_keeps_its_text_in_the_title()
    {
        var text = new string('x', GridCell.ShortLength + 1);
        var cell = GridCell.Of(Field(FieldType.String), text);

        cell.Short.ShouldBeFalse();
        cell.Title.ShouldBe(text);
    }

    [Fact]
    public void Only_text_is_ever_long()
        => GridCell.Of(Field(FieldType.Enum), new string('x', 40)).Short.ShouldBeTrue(
            "a badge, a number or a date is never clipped");

    private static FieldSchema Decimal(int? scale)
        => new() { Name = "total", Type = FieldType.Decimal, Precision = 10, Scale = scale };

    private static FieldSchema Field(FieldType type) => new() { Name = "value", Type = type };
}
