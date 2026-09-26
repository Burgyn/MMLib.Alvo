using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every text input the dashboard draws has one look (spec §3.8): the value at <c>--text-sm</c> (<c>--text-lg</c> on a
/// phone), one box with a <c>--radius-xs</c> corner, one single-line height, one name, above the box and never on its
/// border, and its hint linked to it. In both themes, and on the static sign-in page too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, because the defect was three looks that each rendered.</b> One editor held plain boxes named from
/// above at 14 px, the library's outlined boxes named in a notch on the border at 16 px, and checkbox labels at 16 px,
/// and the typed-name confirm's outline struck through its own label. Every scenario in the suite passed over all
/// of it.
/// </para>
/// <para>
/// <b>The heights are compared across every screen a fact visits, the sign-in page included</b>, so a box that is one
/// pixel taller on one screen fails here and not in a screenshot review. Each expected field is waited for by its
/// name before the screen is read: the filter, the Data search, the rules and the AI form draw after a load.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldConsistencyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task Every_field_on_the_schema_screens_has_the_look_of_the_sign_in_page(ColorScheme scheme)
    {
        var fields = new FieldLook();
        await fields.ReadSignInAsync(world, scheme);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);

        await session.GoAsync("/schema");
        await fields.ReadAsync(session.Content, "the entity filter", expect: ["Filter entities"]);

        await session.Button("New entity", exact: true).ClickAsync();
        await fields.ReadAsync(session.Dialog("new-entity"), "the new entity editor", expect: ["Name"]);
        await session.Page.Keyboard.PressAsync("Escape");

        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await fields.ReadAsync(session.Dialog("field-sheet"), "the new field editor", expect: ["Name", "Max length"]);
        await session.Page.Keyboard.PressAsync("Escape");

        await session.OpenTabAsync("Rules");
        await fields.ReadAsync(session.Content, "the rules editor", expect: ["GET /api/work_orders", "POST /api/work_orders"]);

        fields.Failures.ShouldBeEmpty();
        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task Every_field_in_the_record_and_person_editors_has_the_look_of_the_sign_in_page(ColorScheme scheme)
    {
        var fields = new FieldLook();
        await fields.ReadSignInAsync(world, scheme);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);

        await session.GoAsync("/data/regions");
        await fields.ReadAsync(session.Content, "the data search", expect: ["Search regions"]);
        await session.Button("New record", exact: true).ClickAsync();
        await fields.ReadAsync(session.Dialog("record-sheet"), "the record editor", expect: ["Code", "Name"]);
        await session.Page.Keyboard.PressAsync("Escape");

        var email = $"look-{scheme}@example.com".ToLowerInvariant();
        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await fields.ReadAsync(session.Dialog("person-create"), "the new person editor", expect: ["Email"]);
        await session.Page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.SnackbarAsync($"Created {email}");

        /* The one field that carries a refusal and a linked hint together. */
        await session.Button($"Change {email}", exact: true).ClickAsync();
        await fields.ReadAsync(session.Dialog("person-editor"), "the person editor", expect: ["Tenant"]);

        fields.Failures.ShouldBeEmpty();
        session.AssertConsoleClean();
    }

    /// <summary>On a phone a value is typed at 16 px, which is what keeps iOS from zooming into the field on focus.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_phone_every_field_is_typed_at_16_px_and_still_has_one_height()
    {
        var fields = new FieldLook();
        await fields.ReadSignInAsync(world, ColorScheme.Light, width: 390);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 390);

        await session.GoAsync("/data/regions");
        await session.Button("New record", exact: true).ClickAsync();
        await fields.ReadAsync(session.Dialog("record-sheet"), "the record editor on a phone", expect: ["Code", "Name"]);

        fields.Failures.ShouldBeEmpty();
    }
}

/// <summary>
/// The typed-name confirms and the apply's reason have the one look too: the confirm is where the library's outline
/// struck through its own label.
/// </summary>
/// <remarks>
/// Its own world, for <see cref="DestructiveApplyConfirmScenarios"/>' reason: it stages a drop, and one operator's
/// working copy is shared by every scenario of a world. Both themes are read inside the one fact for the same reason:
/// the drop is staged once, and each theme's session previews it.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ConfirmFieldConsistencyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private const string TypeTheName = "Type field-service to allow it";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_reason_and_the_typed_name_confirms_have_the_look_of_the_sign_in_page()
    {
        var fields = new FieldLook();
        await fields.ReadSignInAsync(world, ColorScheme.Light);
        await using (var session = await world.SignInAsync(TestContext.Current.CancellationToken))
        {
            await HistoryScenarios.ApplyNewEntityAsync(session, "gauges");
        }

        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);
            await ReadRollbackConfirmAsync(session, fields, scheme);
        }

        await using (var session = await world.SignInAsync(TestContext.Current.CancellationToken))
        {
            await session.GoAsync("/schema/regions");
            await session.Page.GetByTestId("remove-field-code").ClickAsync();
            await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        }

        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);
            await ReadApplyConfirmAsync(session, fields, scheme);
        }

        fields.Failures.ShouldBeEmpty();
    }

    private static async Task ReadRollbackConfirmAsync(AdminSession session, FieldLook fields, ColorScheme scheme)
    {
        await session.GoAsync("/history");
        await HistoryScenarios.PlanTheRollbackOfTheNewestAsync(session);
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        var confirm = session.Dialog("rollback-confirm");
        await fields.ReadAsync(confirm, $"the rollback confirm ({scheme})", expect: [TypeTheName]);
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private static async Task ReadApplyConfirmAsync(AdminSession session, FieldLook fields, ColorScheme scheme)
    {
        await session.PreviewPendingAsync();
        await fields.ReadAsync(session.Content, $"the apply's reason ({scheme})", expect: ["Why"]);
        await session.Button("Apply these changes").ClickAsync();
        var confirm = session.Dialog("apply-confirm");
        await fields.ReadAsync(confirm, $"the apply confirm ({scheme})", expect: [TypeTheName]);
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}

/// <summary>The AI connection form and the assistant's question have the one look too.</summary>
/// <param name="world">A host with an agent installed, a writable secret store and no connection saved.</param>
public sealed class AssistantFieldConsistencyScenarios(ConfigurableAssistantWorld world)
    : IClassFixture<ConfigurableAssistantWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task The_connection_form_and_the_question_have_the_look_of_the_sign_in_page(ColorScheme scheme)
    {
        var fields = new FieldLook();
        await fields.ReadSignInAsync(world, scheme);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);

        await session.GoAsync("/settings");
        await fields.ReadAsync(session.Content, "the AI connection form", expect: ["Endpoint", "Model", "API key"]);
        if (await session.Page.GetByTestId("assistant-launch").CountAsync() == 0)
        {
            await session.Content.GetByLabel("Endpoint", new() { Exact = true }).FillAsync("http://127.0.0.1:1/v1");
            await session.Content.GetByLabel("Model", new() { Exact = true }).FillAsync("scripted");
            await session.Page.GetByTestId("ai-save").ClickAsync();
        }

        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        var pane = session.Page.GetByRole(AriaRole.Complementary, new() { Name = "Ask Alvo", Exact = true });
        await fields.ReadAsync(pane, "the assistant's question", expect: ["Your question"]);

        fields.Failures.ShouldBeEmpty();
        session.AssertConsoleClean();
    }
}

/// <summary>
/// The rule of spec §3.8 as measurements, gathered across the screens one fact visits so the single-line height is
/// compared between them.
/// </summary>
internal sealed class FieldLook
{
    private readonly List<(string Where, string Id, double Height)> _singleLine = [];

    /// <summary>Everything that broke the rule, as <c>where: field: what</c>.</summary>
    public List<string> Failures { get; } = [];

    /// <summary>Reads the static sign-in page, in a context of its own, before anybody signs in.</summary>
    /// <param name="world">The running host.</param>
    /// <param name="scheme">The system theme.</param>
    /// <param name="width">The viewport width.</param>
    public async Task ReadSignInAsync(AdminWorld world, ColorScheme scheme, int width = 1400)
    {
        await using var context = await world.Browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = width, Height = width < 720 ? 780 : 950 },
            ColorScheme = scheme,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.SignInPath}");
        await ReadAsync(page.Locator("body"), $"the sign-in page ({scheme}, {width} px)", expect: ["Email", "Password"]);
    }

    /// <summary>
    /// Waits for every expected field by its name, then reads the fields under <paramref name="root"/> and records every
    /// breach of the rule.
    /// </summary>
    /// <param name="root">The screen or the dialog.</param>
    /// <param name="where">What it is, for the failure message.</param>
    /// <param name="expect">Names, or parts of names, of fields that must be among those read.</param>
    public async Task ReadAsync(ILocator root, string where, string[] expect)
    {
        foreach (var name in expect)
        {
            await root.GetByLabel(name).First.WaitForAsync();
        }

        var reading = await FieldProbe.ReadAsync(root);
        foreach (var missing in expect.Where(name => reading.Fields.All(field => !field.Name.Contains(name, StringComparison.Ordinal))))
        {
            Failures.Add($"{where}: {missing}: not read — the scenario did not reach it");
        }

        foreach (var field in reading.Fields)
        {
            Judge(where, field, reading);
        }

        foreach (var check in reading.Checks.Where(check => check.FontSize != reading.TextSize))
        {
            Failures.Add($"{where}: the checkbox '{check.Text}': its label is {check.FontSize}, not {reading.TextSize}");
        }

        CompareHeights();
    }

    private void Judge(string where, FieldProbe.Field field, FieldProbe.Reading reading)
    {
        void Fail(string what) => Failures.Add($"{where}: {field}: {what}");

        if (field.FontSize != reading.ValueSize)
        {
            Fail($"its text is {field.FontSize}, not {reading.ValueSize}");
        }

        if (field.Radius != reading.Radius || field.BorderWidth != "1px" || field.BorderStyle != "solid")
        {
            Fail($"its box is {field.BorderWidth} {field.BorderStyle} at {field.Radius}, not 1px solid at {reading.Radius}");
        }

        if (field.ExtraNames > 0)
        {
            Fail($"{field.ExtraNames} name(s) drawn beside its one label, the library's own label among them");
        }

        if (field.UnlinkedHints > 0)
        {
            Fail($"{field.UnlinkedHints} hint(s) under it are not in its aria-describedby");
        }

        JudgeName(field, reading, Fail);
        if (!field.MultiLine)
        {
            _singleLine.Add((where, field.ToString(), field.Box.Height));
        }
    }

    private static void JudgeName(FieldProbe.Field field, FieldProbe.Reading reading, Action<string> fail)
    {
        if (field.Label is not { } label)
        {
            if (field.AriaLabel.Length == 0)
            {
                fail("it has no visible name and no aria-label");
            }

            return;
        }

        var box = field.Box;
        if (label.Bottom > box.Top + 0.5)
        {
            fail($"its name ends at {label.Bottom:0.#} px, below the top of its box at {box.Top:0.#} px");
        }

        var overlaps = label.Right > box.Left && label.Left < box.Right && label.Bottom > box.Top && label.Top < box.Bottom;
        if (overlaps)
        {
            fail("its name crosses its box's border");
        }

        if (Math.Abs(label.Left - box.Left) > 1)
        {
            fail($"its name starts at {label.Left:0.#} px and its box at {box.Left:0.#} px");
        }

        if (field.LabelFontSize != reading.TextSize)
        {
            fail($"its name is {field.LabelFontSize}, not {reading.TextSize}");
        }
    }

    private void CompareHeights()
    {
        if (_singleLine.Count == 0)
        {
            return;
        }

        var first = _singleLine[0];
        foreach (var other in _singleLine.Where(entry => Math.Abs(entry.Height - first.Height) > 0.5).ToList())
        {
            Failures.Add($"{other.Where}: {other.Id}: a single-line box {other.Height:0.#} px tall, "
                + $"where {first.Where}'s {first.Id} is {first.Height:0.#} px");
            _singleLine.Remove(other);
        }
    }
}
