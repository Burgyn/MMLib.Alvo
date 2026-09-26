using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
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

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        base.Configure(services);
        var shipped = services.Last(entry => entry.ServiceType == typeof(IAlvoManagement) && !entry.IsKeyedService);
        services.Remove(shipped);
        services.Add(new ServiceDescriptor(
            typeof(IAlvoManagement), provider => new Armed(Build(shipped, provider), this), shipped.Lifetime));
    }

    private static IAlvoManagement Build(ServiceDescriptor shipped, IServiceProvider provider)
        => (IAlvoManagement)(shipped.ImplementationFactory?.Invoke(provider)
            ?? shipped.ImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, shipped.ImplementationType!));

    /// <summary>The shipped port, with the info read and the connection save armable.</summary>
    private sealed class Armed(IAlvoManagement inner, SettingsWorld world) : IAlvoManagement
    {
        public async Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default)
        {
            if (world.InfoDelay > TimeSpan.Zero)
            {
                await Task.Delay(world.InfoDelay, ct).ConfigureAwait(false);
            }

            return await inner.GetInfoAsync(ct).ConfigureAwait(false);
        }

        public Task SetAiConnectionAsync(StoredAiConnection connection, CancellationToken ct = default)
            => world.RefuseSaves is { } refusal ? throw refusal() : inner.SetAiConnectionAsync(connection, ct);

        public Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default)
            => inner.ListProjectsAsync(ct);

        public Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default)
            => inner.GetDescriptorAsync(project, ct);

        public Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(string project, CancellationToken ct = default)
            => inner.ListRevisionsAsync(project, ct);

        public Task<ManagementRevisionDetail> GetRevisionAsync(string project, int revision, CancellationToken ct = default)
            => inner.GetRevisionAsync(project, revision, ct);

        public Task<SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default)
            => inner.GetSchemaAsync(project, ct);

        public Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default)
            => inner.GetCapabilitiesAsync(project, ct);

        public Task<ManagementPolicyVerdict> SimulatePolicyAsync(
            string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
            => inner.SimulatePolicyAsync(project, simulation, ct);

        public Task<ManagementApplyResult> ApplyDescriptorAsync(
            string project, ManagementApplyRequest request, CancellationToken ct = default)
            => inner.ApplyDescriptorAsync(project, request, ct);

        public Task<ManagementApplyResult> RollbackAsync(
            string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)
            => inner.RollbackAsync(project, targetRevision, request, ct);
    }
}

/// <summary>Saving the AI connection is a snackbar, not a word left beside the button (spec §3.3; inventory §2d.3).</summary>
/// <param name="world">A host with an agent installed and a writable secret store.</param>
public sealed class SettingsScenarios(SettingsWorld world) : IClassFixture<SettingsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Saving_the_connection_says_so_once_and_leaves_nothing_behind()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.SnackbarAsync("Saved the AI connection");
        (await session.SnackbarCountAsync("Saved the AI connection")).ShouldBe(1, "one save, said once");
        (await session.Content.GetByText("Saved.", new() { Exact = true }).CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
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

        await session.Page.FillAsync("#ai-endpoint", endpoint);
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.Content.GetByTestId("field-problem").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#ai-endpoint");
        (await session.Page.Locator("#ai-endpoint").GetAttributeAsync("aria-invalid")).ShouldBe("true");
        (await session.SnackbarCountAsync()).ShouldBe(0, "a refused save never says it saved");

        /* Box, hint, then the refusal (spec §3.8): the hint that says what to type stays under the box. */
        var hint = (await session.Page.Locator("#ai-endpoint-hint").BoundingBoxAsync()).ShouldNotBeNull();
        var problem = (await session.Content.GetByTestId("field-problem").BoundingBoxAsync()).ShouldNotBeNull();
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
            await session.Page.GetByTestId("ai-save").WaitForAsync();
        }
        finally
        {
            world.InfoDelay = TimeSpan.Zero;
        }
    }
}
