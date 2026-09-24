using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>A box's row never lets its name and its detail run into each other.</summary>
/// <remarks>The four rows the bike-workshop map drew overlapping, at the box width the layout ships.</remarks>
public sealed class MapTextTests
{
    [Fact]
    public void A_row_holds_what_the_box_width_allows()
        => MapText.RowChars.ShouldBe((int)((MapLayout.BoxWidth - 2 * MapText.Inset) / MapText.CharWidth));

    [Theory]
    [InlineData("service_orders_count", "Σ count from service_orders", "service_orders_count", "Σ count from service_…")]
    [InlineData("parts_total", "Σ sum line_total from order_lines", "parts_total", "Σ sum line_total from order_li…")]
    [InlineData("fleet_bike_id", "→ rental_fleet · restrict", "fleet_bike_id", "→ rental_fleet · restrict")]
    [InlineData("orders_assigned", "Σ count from service_orders", "orders_assigned", "Σ count from service_orders")]
    public void The_detail_is_cut_before_the_name(string name, string detail, string shownName, string shownDetail)
    {
        var (fitName, fitDetail) = MapText.Fit(name, detail, MapText.RowChars);

        (fitName, fitDetail).ShouldBe((shownName, shownDetail));
        (fitName.Length + fitDetail.Length).ShouldBeLessThanOrEqualTo(MapText.RowChars - MapText.Gap);
    }

    [Fact]
    public void A_name_too_long_for_the_row_is_cut_once_the_detail_is_short()
        => MapText.Fit("a_very_long_field_name_that_goes_on_and_on_forever", "string", 44)
            .ShouldBe(("a_very_long_field_name_that_goes_on…", "string"));
}
