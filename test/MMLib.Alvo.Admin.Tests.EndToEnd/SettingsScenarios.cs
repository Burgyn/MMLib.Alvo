using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Management;
using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A host with an agent installed and a writable secret store, whose management port can be told to answer its
/// info slowly or to refuse a connection save.
/// </summary>
/// <remarks>
/// <b>The one service this world stands in for</b> is <see cref="IAlvoManagement"/>, wrapped so two members can
/// be armed: <see cref="IAlvoManagement.GetInfoAsync"/> waits <see cref="InfoDelay"/> first, and
/// <see cref="IAlvoManagement.SetAiConnectionAsync"/> throws what <see cref="RefuseSaves"/> names. Everything else,
/// and both of those when unarmed, goes to the real port.
/// </remarks>
public sealed class SettingsWorld : ConfigurableAssistantWorld
{
    /// <summary>How long the info read waits before it runs; zero leaves it as shipped.</summary>
    public TimeSpan InfoDelay { get; set; }

    /// <summary>What a connection save throws instead of writing, or <see langword="null"/> to write.</summary>
    public Func<Exception>? RefuseSaves { get; set; }

    /// <summary>Whether every info read throws, the way a store that is briefly unreachable does.</summary>
    public bool FailInfoReads { get; set; }

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        base.Configure(services);
        ManagementDecorator.Around(services, shipped => new Armed(shipped, this));
    }

    /// <summary>The shipped port, with the info read and the connection save armable.</summary>
    private sealed class Armed(IAlvoManagement inner, SettingsWorld world) : ManagementDecorator(inner)
    {
        public override async Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default)
        {
            if (world.FailInfoReads)
            {
                throw new InvalidOperationException("The info store did not answer.");
            }

            if (world.InfoDelay > TimeSpan.Zero)
            {
                await Task.Delay(world.InfoDelay, ct).ConfigureAwait(false);
            }

            return await base.GetInfoAsync(ct).ConfigureAwait(false);
        }

        public override Task SetAiConnectionAsync(StoredAiConnection connection, CancellationToken ct = default)
            => world.RefuseSaves is { } refusal ? throw refusal() : base.SetAiConnectionAsync(connection, ct);
    }
}

/// <summary>
/// The AI connection is changed in an editor (spec §3.1): a real form, where Enter submits, the dirty guard asks and a
/// save is a snackbar, not a word left beside the button (spec §3.3, §3.4; inventory §2d.3; final review I3).
/// </summary>
/// <param name="world">A host with an agent installed and a writable secret store.</param>
public sealed class SettingsScenarios(SettingsWorld world) : IClassFixture<SettingsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_saves_the_connection_says_so_once_and_leaves_nothing_behind()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        var editor = await OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.Locator("#ai-model").PressAsync("Enter");

        await session.SnackbarAsync("Saved the AI connection");
        (await session.SnackbarCountAsync("Saved the AI connection")).ShouldBe(1, "one save, said once");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusOnTestIdAsync("ai-change");
        (await session.Content.GetByText("Saved.", new() { Exact = true }).CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    /// <summary>A typed connection is not lost to Escape without a question; an untouched editor closes at once.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_asks_before_it_loses_a_typed_connection()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        var editor = await OpenConnectionEditorAsync(session);
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        editor = await OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#ai-model")).ShouldBe("scripted", "Keep editing keeps what was typed");

        await editor.GetByTestId("editor-cancel").ClickAsync();
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusOnTestIdAsync("ai-change");
        (await session.SnackbarCountAsync()).ShouldBe(0, "nothing was saved");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A save whose read-back failed still happened: the editor says so, and closing it asks nothing, because the
    /// connection it holds is the one stored (final-fix-A re-review, finding 2).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task After_a_save_whose_read_back_failed_closing_asks_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");
        var editor = await OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");

        world.FailInfoReads = true;
        try
        {
            await session.Page.Locator("#ai-model").PressAsync("Enter");
            (await session.Page.GetByTestId("error-title").InnerTextAsync())
                .ShouldBe("The connection was saved, and this screen could not read it back");
        }
        finally
        {
            world.FailInfoReads = false;
        }

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("editor-discard-question").CountAsync()).ShouldBe(0, "nothing is lost by closing");
    }

    /// <summary>
    /// A saved keyless connection to a local endpoint says so, and nothing warns (§8d item 31: "no key needed").
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_saved_keyless_local_connection_says_no_key_is_needed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");
        await SaveAsync(session, "http://127.0.0.1:1/v1", key: null);

        await session.Content.GetByText("no key needed", new() { Exact = true }).WaitForAsync();
        (await session.Page.GetByTestId("ai-key-missing").CountAsync()).ShouldBe(0, "a keyless local model is not a fault");
        (await session.Content.GetByText("connected", new() { Exact = true }).CountAsync()).ShouldBe(1);
        session.AssertConsoleClean();
    }

    /// <summary>A saved connection with its key adds nothing to the status — and the key is never drawn back.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_saved_connection_with_its_key_adds_nothing()
    {
        const string key = "sk-e2e-present-key";
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");
        await SaveAsync(session, "https://api.openai.com/v1", key);

        await session.Content.GetByText("connected", new() { Exact = true }).WaitForAsync();
        (await session.Page.GetByTestId("ai-key-missing").CountAsync()).ShouldBe(0);
        (await session.Content.GetByText("no key needed", new() { Exact = true }).CountAsync()).ShouldBe(0);
        (await session.Page.ContentAsync()).ShouldNotContain(key, Case.Sensitive, "the key is never shown back");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// A saved connection to a host that always needs a key, saved without one, is the warning — and its action opens
    /// the connection editor, because here the fix is this screen's (spec §3.3: the fix is the alert's action).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_saved_connection_missing_its_key_offers_to_change_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");
        await SaveAsync(session, "https://api.openai.com/v1", key: null);

        var alert = session.Page.GetByTestId("ai-key-missing");
        await alert.WaitForAsync();
        await alert.GetByRole(AriaRole.Button, new() { Name = "Change the connection" }).ClickAsync();
        await session.Dialog("ai-editor").GetByTestId("ai-save").WaitForAsync();
        await session.WaitForFocusInsideAsync("ai-editor");
        session.AssertConsoleClean();
    }

    /// <summary>Saves a connection through the editor and waits for the screen to say so.</summary>
    private static async Task SaveAsync(AdminSession session, string endpoint, string? key)
    {
        await OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-endpoint", endpoint);
        await session.Page.FillAsync("#ai-model", "scripted");
        if (key is not null)
        {
            await session.Page.FillAsync("#ai-key", key);
        }

        await session.Page.GetByTestId("ai-save").ClickAsync();
        await session.SnackbarAsync("Saved the AI connection");
    }

    /// <summary>Opens the connection editor from its summary and waits for focus inside it.</summary>
    internal static async Task<ILocator> OpenConnectionEditorAsync(AdminSession session)
    {
        await session.Page.GetByTestId("ai-change").ClickAsync();
        var editor = session.Dialog("ai-editor");
        await editor.GetByTestId("ai-save").WaitForAsync();
        await session.WaitForFocusInsideAsync("ai-editor");
        return editor;
    }

    /// <summary>
    /// An endpoint that is not an http or https address is refused under its field, which takes focus, and nothing
    /// says it saved.
    /// </summary>
    /// <remarks>
    /// The store takes any text, and the resolver then reads a connection it cannot build as none at all: the
    /// screen would say saved and the status would stay "not configured", with nothing saying why.
    /// <c>localhost:11434/v1</c> is the case a plain "absolute address" check lets through, as the scheme
    /// <c>localhost</c>.
    /// </remarks>
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("localhost")]
    [InlineData("localhost:11434/v1")]
    public async Task An_endpoint_that_is_not_a_web_address_is_refused_under_its_field(string endpoint)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        await OpenConnectionEditorAsync(session);
        await session.Page.FillAsync("#ai-endpoint", endpoint);
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.Page.GetByTestId("field-problem").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#ai-endpoint");
        (await session.Page.Locator("#ai-endpoint").GetAttributeAsync("aria-invalid")).ShouldBe("true");
        (await session.SnackbarCountAsync()).ShouldBe(0, "a refused save never says it saved");

        /* Box, hint, then the refusal (spec §3.8): the hint that says what to type stays under the box. */
        var hint = (await session.Page.Locator("#ai-endpoint-hint").BoundingBoxAsync()).ShouldNotBeNull();
        var problem = (await session.Page.GetByTestId("field-problem").BoundingBoxAsync()).ShouldNotBeNull();
        ((double)problem.Y).ShouldBeGreaterThanOrEqualTo(hint.Y + hint.Height - 0.5, "the refusal sits under the hint");
        session.AssertConsoleClean();
    }

    /// <summary>A save the store refuses is an alert in place, which takes focus, and never a snackbar (spec §3.3).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_save_is_an_alert_that_takes_focus()
    {
        world.RefuseSaves = () => new SecretShadowedException(
            SecretName.Parse(StoredAiConnection.SecretName), "this deployment's Alvo:Ai configuration");
        try
        {
            await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
            await session.GoAsync("/settings");

            await OpenConnectionEditorAsync(session);
            await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
            await session.Page.FillAsync("#ai-model", "scripted");
            await session.Page.GetByTestId("ai-save").ClickAsync();

            await session.Page.GetByTestId("error-panel").WaitForAsync();
            (await session.Page.GetByTestId("error-title").InnerTextAsync()).ShouldBe("That connection could not be saved");
            (await session.FocusedAsync()).ShouldEndWith("[error-panel]");
            (await session.SnackbarCountAsync()).ShouldBe(0, "no error is ever a snackbar");
        }
        finally
        {
            world.RefuseSaves = null;
        }
    }

    /// <summary>
    /// While the page loads it shows the labelled skeleton, so a screen reader hears "loading" and a sighted operator
    /// sees the shape of what is coming (spec §3.6).
    /// </summary>
    /// <remarks>
    /// The prerendered page waits for its load and arrives whole; the circuit then builds the screen again with a
    /// gateway of its own (scoped per circuit, so its cache starts cold), and its first render is the loading branch.
    /// On a local host that read is too fast for the branch to stay on screen long enough to wait for, so the info
    /// read is slowed.
    /// </remarks>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task While_the_page_loads_it_shows_its_shape_not_a_blank()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        world.InfoDelay = TimeSpan.FromSeconds(3);
        try
        {
            await session.Page.GotoAsync($"{world.BaseAddress}{AlvoAdmin.BasePath}/settings");
            await session.Page.GetByLabel("Loading").First.WaitForAsync();
            await session.Page.GetByTestId("ai-change").WaitForAsync();
        }
        finally
        {
            world.InfoDelay = TimeSpan.Zero;
        }
    }
}

/// <summary>
/// A host whose deployment pins a connection to an endpoint that always needs a key, and whose
/// <c>Alvo:Ai:ApiKeySecretRef</c> names a secret nobody saved — the live case of §8d item 31 (24 Sep 2026).
/// </summary>
/// <remarks>
/// The resolver is the real one, as <see cref="ConfigurableAssistantWorld"/>'s is: the question is whether the
/// shipped read path says the key is missing, and a substituted resolver would answer it by construction.
/// </remarks>
public sealed class MissingKeyWorld : ConfigurableAssistantWorld
{
    /// <summary>The secret the configuration names and nothing has written.</summary>
    internal const string AbsentSecret = "openai.key";

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings)
    {
        base.Configure(settings);
        settings["Alvo:Ai:Kind"] = StoredAiConnection.OpenAiCompatibleKind;
        settings["Alvo:Ai:Endpoint"] = "https://api.openai.com/v1";
        settings["Alvo:Ai:Model"] = "gpt-5";
        settings["Alvo:Ai:ApiKeySecretRef"] = AbsentSecret;
    }
}

/// <summary>
/// Settings never reads "connected" for a connection whose key is missing: it says every request will be refused,
/// and where the fix is (docs/todo-admin.md §8d item 31).
/// </summary>
/// <param name="world">A host whose configured key reference names an absent secret.</param>
public sealed class MissingKeyScenarios(MissingKeyWorld world) : IClassFixture<MissingKeyWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_reference_to_an_absent_secret_is_a_warning_with_its_fix_not_connected()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        var alert = session.Page.GetByTestId("ai-key-missing");
        await alert.WaitForAsync();
        (await alert.GetAttributeAsync("role")).ShouldBe("alert", "a warning is read out, not only drawn");
        var text = await alert.InnerTextAsync();
        text.ShouldContain("Key missing — every request will be refused");
        text.ShouldContain("Alvo:Ai:ApiKeySecretRef", Case.Sensitive, "the fix names where the reference is set");

        (await session.Content.GetByText("key missing", new() { Exact = true }).CountAsync())
            .ShouldBe(1, "the status says the key is missing");
        (await session.Content.GetByText("connected", new() { Exact = true }).CountAsync())
            .ShouldBe(0, "the status never reads \"connected\" alone while the key is missing");
        session.AssertConsoleClean();
    }
}
