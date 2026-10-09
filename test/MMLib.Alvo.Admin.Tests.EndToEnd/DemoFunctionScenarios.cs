using Microsoft.Playwright;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The demo uses the hook functions (Task 16b, the maintainer's request): the before-hooks <c>examples/bike-workshop</c>
/// declares open in place — guided where the condition is a row — with their calls in the expression boxes and the
/// function list offered beside them, and a write through the HTTP Data API stores what they compute.
/// </summary>
/// <remarks>
/// Nothing here edits the working copy, so the scenarios share one world in any order. The expressions are pinned exactly
/// as the descriptor declares them; the README's "Hook functions" table quotes the same text.
/// </remarks>
/// <param name="world">The bike-workshop demo on the shipped host, with a dev key, and a browser.</param>
public sealed class DemoFunctionScenarios(DemoFunctionWorld world) : IClassFixture<DemoFunctionWorld>
{
    /// <summary>The demo's mutate expressions, exactly as the descriptor declares them.</summary>
    internal const string FrameNumber = "upperAscii(replace(trim(new.frame_number), ' ', ''))";
    internal const string RackTag = "upperAscii(new.brand) + ' ' + new.frame_number";
    internal const string WeekRate = "math.round(new.daily_rate * 0.875, 2)";

    /// <summary>The demo's guided conditions.</summary>
    internal const string WorkshopAddress = "endsWith(new.email, '@velo-dielna.example')";
    internal const string AWeekOrLonger = "new.days >= 7";

    /// <summary>The guided operators those conditions open with, as the dashboard words them.</summary>
    internal const string EndsWith = "ends with";
    internal const string AtLeast = "is at least";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_bike_hooks_open_with_their_calls_and_the_functions_they_use_are_offered()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "bikes");

        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-mutate-value-0", FrameNumber);
        var list = await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        foreach (var name in new[] { "upperAscii", "replace", "trim", "math.round" })
        {
            (await list.GetByTestId($"fn-{name}").InnerTextAsync()).ShouldContain("built-in", Case.Sensitive, $"{name} is offered to the demo's mutate box");
        }

        await CloseAsync(session);

        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 1);
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-mutate-value-0", RackTag);
        await CloseAsync(session);

        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeUpdate", 0);
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-mutate-value-0", FrameNumber);
        await CloseAsync(session);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_text_test_and_the_week_discount_open_as_guided_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "customers");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);
        await GuidedConditionScenarios.Mode(session, "Guided").WaitForAsync();
        await GuidedConditionScenarios.ReadoutAsync(session, WorkshopAddress);
        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe(WorkshopAddress);
        (await GuidedConditionScenarios.Combobox(session, "Condition 1 operator").InnerTextAsync()).ShouldContain(EndsWith);
        await CloseAsync(session);

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);
        await GuidedConditionScenarios.Mode(session, "Guided").WaitForAsync();
        await GuidedConditionScenarios.ReadoutAsync(session, AWeekOrLonger);
        (await GuidedConditionScenarios.Combobox(session, "Condition 1 operator").InnerTextAsync()).ShouldContain(AtLeast);
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-mutate-value-0", WeekRate);
        await CloseAsync(session);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_write_through_the_data_api_stores_what_the_demo_hooks_compute()
    {
        using var api = world.Api();
        var customer = Id(await HostFunctionScenarios.CreateAsync(api, "customers", new { first_name = "Ivan", last_name = "Kráľ", phone = "+421 905 000 111" }));

        var bike = await HostFunctionScenarios.CreateAsync(api, "bikes", new
        {
            customer_id = customer,
            brand = "Kellys",
            model = "Spider 30",
            category = "mtb_hardtail",
            frame_number = " ks 25 spi30 m00042 ",
        });
        bike.GetProperty("frame_number").GetString().ShouldBe("KS25SPI30M00042", "trimmed, without spaces, in capitals");
        bike.GetProperty("rack_tag").GetString().ShouldBe("KELLYS KS25SPI30M00042", "the second hook sees the first one's frame number");
        using var tagged = await api.PostAsJsonAsync(
            "/api/bikes",
            new { customer_id = customer, brand = "Kellys", model = "Spider 30", category = "mtb_hardtail", frame_number = "KS25SPI30M00099", rack_tag = "MINE" },
            TestContext.Current.CancellationToken);
        tagged.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "rack_tag is read-only to a caller, though the hook writes it");
        (await tagged.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("rack_tag");

        var recoloured = await PatchAsync(api, $"/api/bikes/{Id(bike)}", new { color = "black" });
        recoloured.GetProperty("frame_number").GetString().ShouldBe("KS25SPI30M00042", "an update that leaves the frame number alone keeps it");
        var renumbered = await PatchAsync(api, $"/api/bikes/{Id(bike)}", new { frame_number = "ks25 spi30m00043" });
        renumbered.GetProperty("frame_number").GetString().ShouldBe("KS25SPI30M00043");
        var read = await api.GetFromJsonAsync<JsonElement>($"/api/bikes/{Id(bike)}", TestContext.Current.CancellationToken);
        read.GetProperty("frame_number").GetString().ShouldBe("KS25SPI30M00043", "stored, not only answered");
        read.GetProperty("rack_tag").GetString().ShouldBe("KELLYS KS25SPI30M00042", "the tag is printed once, at registration");

        var fleet = Id(await HostFunctionScenarios.CreateAsync(api, "rental_fleet", new
        {
            code = "RENT-901",
            brand = "Kellys",
            model = "Estima 40",
            category = "ebike",
            daily_rate = 45.0m,
            deposit = 300.0m,
        }));
        var week = await RentAsync(api, fleet, customer, days: 7, startsInDays: 10);
        week.GetProperty("daily_rate").GetDecimal().ShouldBe(39.38m, "45 less an eighth is 39.375, rounded to the cent");
        week.GetProperty("price").GetDecimal().ShouldBe(275.66m);
        var stored = await api.GetFromJsonAsync<JsonElement>($"/api/rentals/{Id(week)}", TestContext.Current.CancellationToken);
        stored.GetProperty("daily_rate").GetDecimal().ShouldBe(39.38m, "stored, not only answered");
        stored.GetProperty("price").GetDecimal().ShouldBe(275.66m);
        var weekend = await RentAsync(api, fleet, customer, days: 2, startsInDays: 20);
        weekend.GetProperty("daily_rate").GetDecimal().ShouldBe(45.0m, "under a week the condition does not fire");

        using var refused = await api.PostAsJsonAsync(
            "/api/customers", new { first_name = "Desk", last_name = "Copy", phone = "+421 905 000 222", email = "dielna@velo-dielna.example" },
            TestContext.Current.CancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("That is the workshop's own address.");
    }

    private static async Task CloseAsync(AdminSession session)
    {
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("hook-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private static Task<JsonElement> RentAsync(HttpClient api, string fleet, string customer, int days, int startsInDays)
    {
        var starts = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(startsInDays).AddHours(9), TimeSpan.Zero);
        return HostFunctionScenarios.CreateAsync(api, "rentals", new
        {
            fleet_bike_id = fleet,
            customer_id = customer,
            starts_at = starts,
            due_at = starts.AddDays(days).AddHours(9),
            days,
            daily_rate = 45.0m,
        });
    }

    private static async Task<JsonElement> PatchAsync(HttpClient api, string path, object body)
    {
        using var response = await api.PatchAsJsonAsync(path, body, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static string Id(JsonElement row) => row.GetProperty("id").GetString().ShouldNotBeNull();
}
