using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// A stored value as a form control's text, and the text back as the type the data port binds.
/// </summary>
public class FormValueTests
{
    private static readonly Guid _bike = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Theory]
    [InlineData("21.6", "21.60")]
    [InlineData("1.0", "1.00")]
    [InlineData("17", "17.00")]
    public void A_decimal_opens_at_its_declared_scale(string stored, string shown)
        => FormValue.Format(Field(FieldType.Decimal, scale: 2), decimal.Parse(stored, CultureInfo.InvariantCulture))
            .ShouldBe(shown);

    [Fact]
    public void A_decimal_is_written_and_read_invariant_whatever_the_servers_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sk-SK");
            var field = Field(FieldType.Decimal, scale: 2);

            FormValue.Format(field, 21.6m).ShouldBe("21.60");
            FormValue.Parse(field, "21.60").Value.ShouldBe(21.60m);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("21,60")]
    [InlineData("1,000.50")]
    [InlineData("twenty")]
    public void A_decimal_that_is_not_invariant_is_refused_with_what_it_should_look_like(string typed)
    {
        var parsed = FormValue.Parse(Field(FieldType.Decimal, scale: 2), typed);

        parsed.Value.ShouldBeNull();
        parsed.Problem.ShouldNotBeNull().ShouldContain("21.60");
    }

    [Fact]
    public void Each_type_is_read_back_as_the_clr_type_the_port_binds()
    {
        FormValue.Parse(Field(FieldType.Integer), "3").Value.ShouldBe(3L);
        FormValue.Parse(Field(FieldType.Boolean), "true").Value.ShouldBe(true);
        FormValue.Parse(Field(FieldType.Ref), _bike.ToString()).Value.ShouldBe(_bike);
        FormValue.Parse(Field(FieldType.Uuid), _bike.ToString()).Value.ShouldBe(_bike);
        FormValue.Parse(Field(FieldType.Date), "2026-09-24").Value.ShouldBe(new DateOnly(2026, 9, 24));
        FormValue.Parse(Field(FieldType.DateTime), "2026-09-24T14:30").Value
            .ShouldBe(new DateTimeOffset(2026, 9, 24, 14, 30, 0, TimeSpan.Zero));
        FormValue.Parse(Field(FieldType.String), " SO-2026-0163 ").Value.ShouldBe(" SO-2026-0163 ", "a string is sent as typed");
    }

    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Ref)]
    public void An_empty_control_clears_the_field(FieldType type)
        => FormValue.Parse(Field(type), "  ").ShouldBe(FormParse.Empty);

    [Fact]
    public void A_date_and_an_instant_open_as_their_controls_expect()
    {
        FormValue.Format(Field(FieldType.Date), new DateOnly(2026, 9, 16)).ShouldBe("2026-09-16");
        FormValue.Format(Field(FieldType.DateTime), new DateTimeOffset(2026, 9, 16, 10, 5, 0, TimeSpan.FromHours(2)))
            .ShouldBe("2026-09-16T08:05", "a datetime-local control takes no offset, so the instant is shown in UTC");
    }

    [Fact]
    public void A_zoned_datetime_is_shown_in_utc_and_an_unzoned_one_as_it_is()
    {
        var field = Field(FieldType.DateTime);
        var utc = new DateTime(2026, 9, 16, 8, 5, 0, DateTimeKind.Utc);

        FormValue.Format(field, utc.ToLocalTime()).ShouldBe("2026-09-16T08:05");
        FormValue.Format(field, new DateTime(2026, 9, 16, 8, 5, 0, DateTimeKind.Unspecified)).ShouldBe("2026-09-16T08:05");
    }

    [Fact]
    public void An_instant_round_trips_to_the_same_text()
    {
        var field = Field(FieldType.DateTime);
        var shown = FormValue.Format(field, new DateTimeOffset(2026, 9, 16, 10, 5, 0, TimeSpan.Zero));

        FormValue.Format(field, FormValue.Parse(field, shown).Value).ShouldBe(shown);
    }

    [Fact]
    public void Nothing_stored_is_an_empty_control()
        => FormValue.Format(Field(FieldType.Decimal, scale: 2), null).ShouldBeEmpty();

    private static FieldSchema Field(FieldType type, int? scale = null)
        => new() { Name = "value", Type = type, Scale = scale, Precision = scale is null ? null : 10 };
}
