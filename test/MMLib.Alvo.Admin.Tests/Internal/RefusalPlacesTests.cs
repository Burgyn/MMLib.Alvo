using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Every refusal the build publishes is shown on the screen it is about — by an explicit map, because prefix
/// matching dropped four of them (docs/todo-admin.md §8d item 19): <c>entity.update</c> is an action, not an
/// entity facet, and <c>rollup.where</c>, <c>trigger.event</c>, <c>JSONata</c> and <c>bodyFile</c> matched nothing.
/// </summary>
public class RefusalPlacesTests
{
    /// <summary>The slots <c>UnhonouredFeatures.EveryRefusal</c> publishes at a1f1a57.</summary>
    /// <remarks>The screen is passed by name: <see cref="RefusalScreen"/> is internal, and a public test method cannot take it.</remarks>
    [Theory]
    [InlineData("field.validation", nameof(RefusalScreen.FieldEditor))]
    [InlineData("field.default", nameof(RefusalScreen.FieldEditor))]
    [InlineData("rollup.where", nameof(RefusalScreen.FieldEditor))]
    [InlineData("field.validation", nameof(RefusalScreen.FieldsList))]
    [InlineData("field.default", nameof(RefusalScreen.FieldsList))]
    [InlineData("rollup.where", nameof(RefusalScreen.FieldsList))]
    [InlineData("entity.softDelete", nameof(RefusalScreen.EntityHeader))]
    [InlineData("function", nameof(RefusalScreen.OnWrite))]
    [InlineData("http.call", nameof(RefusalScreen.OnWrite))]
    [InlineData("entity.update", nameof(RefusalScreen.OnWrite))]
    [InlineData("JSONata", nameof(RefusalScreen.OnWrite))]
    [InlineData("JSONata", nameof(RefusalScreen.Integrations))]
    [InlineData("email.data", nameof(RefusalScreen.OnWrite))]
    [InlineData("email.data", nameof(RefusalScreen.Integrations))]
    [InlineData("bodyFile", nameof(RefusalScreen.Integrations))]
    [InlineData("trigger.event", nameof(RefusalScreen.Automations))]
    [InlineData("trigger.event", nameof(RefusalScreen.Functions))]
    public void A_published_slot_is_shown_on_its_screen(string slot, string screen)
        => RefusalPlaces.On(Enum.Parse<RefusalScreen>(screen), [Refused(slot)]).ShouldHaveSingleItem().Slot.ShouldBe(slot);

    [Fact]
    public void An_action_type_is_not_mistaken_for_an_entity_facet()
        => RefusalPlaces.On(RefusalScreen.EntityHeader, [Refused("entity.update")]).ShouldBeEmpty();

    [Fact]
    public void A_slot_the_map_does_not_place_is_unplaced_rather_than_dropped()
    {
        var refused = new[] { Refused("entity.realtime"), Refused("bodyFile") };

        RefusalPlaces.Unplaced(refused).ShouldHaveSingleItem().Slot.ShouldBe("entity.realtime");
        Enum.GetValues<RefusalScreen>().ShouldAllBe(screen => RefusalPlaces.On(screen, refused).All(r => r.Slot != "entity.realtime"));
    }

    private static ManagementRefusedFeature Refused(string slot) => new(slot, "consequence", "fix");
}
