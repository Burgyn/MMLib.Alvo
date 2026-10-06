using Microsoft.Playwright;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The maintainer's goal, end to end (spec §11.2): a function registered in C# is listed by the hook editor with its
/// summary and the "this host" badge, inserted into a mutate expression the check finds clean, applied, and evaluated by a
/// Data API write whose stored value is the function's result.
/// </summary>
/// <remarks>
/// It asserts the strings the embedded-host sample's README ("In the dashboard") quotes; <see cref="HostFunctionReadmeTests"/>
/// reads them from the README itself, so a reworded screen or a reworded README fails rather than drifting apart (ruling S).
/// </remarks>
/// <param name="world">The sample's world on the shipped host, with two host functions, and a browser.</param>
public sealed class HostFunctionScenarios(HostFunctionWorld world) : IClassFixture<HostFunctionWorld>
{
    /// <summary>What the README's "In the dashboard" paragraph quotes, each exactly as the screen draws it.</summary>
    internal const string Disclosure = "Functions you can call here";
    internal const string HostBadge = "this host";
    internal const string BuiltInBadge = "built-in";
    internal const string InsertLabel = "Insert";
    internal const string Signature = "normalizeVin(vin: String) -> String";
    internal const string InsertedCall = "normalizeVin(new.vin)";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_function_is_offered_inserted_applied_and_stores_its_result()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "vehicles", "beforeCreate", "vin");

        var list = await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        (await list.Locator("summary").InnerTextAsync()).ShouldStartWith(Disclosure);
        var row = list.GetByTestId("fn-normalizeVin");
        var text = await row.InnerTextAsync();
        text.ShouldContain(Signature);
        text.ShouldContain(HostFunctionWorld.NormalizeVinSummary);
        text.ShouldContain(HostBadge);
        text.ShouldNotContain(BuiltInBadge, Case.Sensitive, "a host function carries one badge, never both");
        (await list.GetByTestId("fn-trim").InnerTextAsync()).ShouldContain(BuiltInBadge);
        var insert = list.GetByTestId("fn-insert-normalizeVin");
        (await insert.InnerTextAsync()).Trim().ShouldBe(InsertLabel);
        await insert.ClickAsync();

        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-mutate-value-0", InsertedCall);
        /* The recorded verdict on exactly the inserted text is the proof: a clean check draws no sentence to wait out. */
        (await world.CheckedAsync(InsertedCall)).Findings.ShouldBeEmpty("the build knows the host's function");

        await AddHookAsync(session);
        await ApplyAsync(session, "Normalise VINs as vehicles are created");

        using var api = world.Api();
        var owner = await CreateAsync(api, "owners", new { name = "Jana Nováková" });
        var vehicle = await CreateAsync(api, "vehicles", new
        {
            /* Lower case and one dash, within the field's 17: the payload is measured before the hook runs (README §3). */
            vin = "wba3a5c5-1cf2569",
            plate = "BA-123XY",
            make = "BMW",
            model = "320i",
            year = 2012,
            owner_id = owner.GetProperty("id").GetString(),
        });

        vehicle.GetProperty("vin").GetString().ShouldBe("WBA3A5C51CF2569");
        var read = await api.GetFromJsonAsync<JsonElement>(
            $"/api/vehicles/{vehicle.GetProperty("id").GetString()}", TestContext.Current.CancellationToken);
        read.GetProperty("vin").GetString().ShouldBe("WBA3A5C51CF2569", "stored, not only answered");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_summary_is_text_never_markup()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "inspections", "beforeUpdate", "notes");

        var row = (await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0")).GetByTestId("fn-shout");
        (await row.InnerTextAsync()).ShouldContain("<b>bold</b>");
        (await row.Locator("b").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    /// <summary>Adds the hook the editor holds and waits for the sheet to close.</summary>
    internal static async Task AddHookAsync(AdminSession session)
    {
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>Applies the working copy from Preview with <paramref name="reason"/>, and waits for the revision.</summary>
    internal static async Task ApplyAsync(AdminSession session, string reason)
    {
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", reason);
        await session.Button("Apply these changes").ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();
    }

    /// <summary>Creates a row through the HTTP Data API and answers it, failing with the problem document otherwise.</summary>
    internal static async Task<JsonElement> CreateAsync(HttpClient api, string entity, object body)
    {
        using var response = await api.PostAsJsonAsync($"/api/{entity}", body, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
