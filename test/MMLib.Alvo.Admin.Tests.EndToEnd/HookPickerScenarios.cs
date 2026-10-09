using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An after-hook names a declared endpoint or template, picked from the working copy, and its payload and recipient are
/// judged by the build as they are typed (spec §4.4, §4.5; rulings B4, B5).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class HookPickerScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_endpoint_picker_offers_the_declared_endpoint_and_writes_its_name()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "rentals", "afterUpdate", "webhook");

        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");
        /* Focus stays on the picker, inside the sheet, on a choice and on a re-choice of the value it already shows. */
        await session.WaitForFocusInsideAsync("hook-endpoint");
        await session.ChooseAgainAsync(Combobox(session, "Endpoint"), "rental-desk", "hook-endpoint");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-afterUpdate-0").InnerTextAsync()).ShouldContain("\"endpoint\": \"rental-desk\"");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_raw_jsonata_payload_gets_the_builds_own_refusal_and_a_broken_condition_does_not_hide_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "rentals", "afterDelete", "webhook");
        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");
        await session.TypeConditionAsync("old.nope == 1");

        await session.Page.FillAsync("#hook-payload", "{\"id\": \"{{old.id}}\"}");
        var finding = session.Page.GetByTestId("check-hook-payload").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("JSONata transformations are not evaluated yet");
        await ShouldKeepFocusAsync(session, "hook-payload");

        await session.Page.FillAsync("#hook-payload", "[\"{{old.id}}\"]");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "hook-payload");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_email_picks_its_template_and_a_second_recipient_is_flagged()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "service_orders", "afterDelete", "email");

        await Combobox(session, "Template").ClickAsync();
        await Option(session, "order-ready").WaitForAsync();
        (await Option(session, "express-order-received").CountAsync()).ShouldBe(1);
        await Option(session, "order-ready").ClickAsync();
        await session.WaitForFocusInsideAsync("hook-template");

        await session.Page.FillAsync("#hook-to", "a@example.com, b@example.com");
        var finding = session.Page.GetByTestId("check-hook-to").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("not exactly one mailbox");
        await ShouldKeepFocusAsync(session, "hook-to");

        await session.Page.FillAsync("#hook-to", "ops@example.com");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        await ShouldKeepFocusAsync(session, "hook-to");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_payload_over_eight_thousand_characters_is_said_under_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "bikes", "afterCreate", "webhook");
        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");

        await session.Page.FillAsync("#hook-payload", "[" + new string('1', 8000) + "]");

        var sentence = session.Page.GetByTestId("hook-payload-length");
        await sentence.WaitForAsync();
        (await sentence.InnerTextAsync()).ShouldContain("8000");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_webhook_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await NewAfterHookAsync(session, "parts", "afterUpdate", "webhook");
        await session.Page.FillAsync("#hook-payload", "[\"{{new.sku}}\", {{new.unit_price}}]");

        await session.AssertNoHorizontalScrollAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_email_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await NewAfterHookAsync(session, "service_orders", "afterUpdate", "email");
        await session.ChooseAsync(Combobox(session, "Template"), "order-ready");
        await session.Page.FillAsync("#hook-to", "{{new.contact_email}}");

        await session.AssertNoHorizontalScrollAsync();
    }

    internal static async Task NewAfterHookAsync(AdminSession session, string entity, string point, string kind)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = kind, Exact = true }).ClickAsync();
    }

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });

    /// <summary>A check sentence arriving under a box leaves focus in the box (spec §12 criterion 3).</summary>
    private static async Task ShouldKeepFocusAsync(AdminSession session, string id)
        => (await session.Page.EvaluateAsync<string>("document.activeElement?.id ?? ''")).ShouldBe(id);
}

/// <summary>
/// A hook naming an endpoint the copy does not declare keeps that name in the picker (spec §4.4) — its own world, because
/// the descriptor arrives by import.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class UndeclaredReferenceScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_naming_an_undeclared_endpoint_keeps_it_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["entities"]!["rentals"]!["hooks"]!["afterCreate"]![0]!["action"]!["endpoint"] = "gone-desk";
        await session.ImportByChordAsync(descriptor.ToJsonString());

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "afterCreate", 0);

        (await session.Page.GetByTestId("hook-endpoint-hint").InnerTextAsync()).ShouldContain("'gone-desk' is not declared");
        await HookPickerScenarios.Combobox(session, "Endpoint").ClickAsync();
        await session.Page.GetByRole(AriaRole.Option, new() { Name = "rental-desk", Exact = true }).WaitForAsync();
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "gone-desk (not declared)", Exact = true }).CountAsync()).ShouldBe(1);
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "rental-desk", Exact = true }).CountAsync()).ShouldBe(1);
    }
}
