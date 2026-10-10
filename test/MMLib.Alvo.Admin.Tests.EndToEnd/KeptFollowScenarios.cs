using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A world whose next real apply can be held on the wire after it wrote, so a scenario can stage an edit and let
/// somebody else apply before the dashboard hears back: the one order in which the copy has to keep its old base.
/// </summary>
/// <remarks>
/// <b>The one service this world stands in for</b> is <see cref="IAlvoManagement"/>, wrapped so that the apply
/// <see cref="HoldNextApply"/> armed runs, says so through <see cref="Written"/>, and returns only once
/// <see cref="Release"/> is called. Every call, that one included, still goes to the real service.
/// </remarks>
public sealed class HeldApplyWorld : AdminWorld
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _written;
    private TaskCompletionSource? _release;

    /// <summary>Completes once the held apply has written its revision.</summary>
    public Task Written => _written?.Task ?? throw new InvalidOperationException("Nothing is held.");

    /// <summary>Arms the next real apply to wait, after it wrote, until <see cref="Release"/>.</summary>
    public void HoldNextApply()
    {
        lock (_gate)
        {
            _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Lets the held apply answer the dashboard.</summary>
    public void Release() => _release?.TrySetResult();

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        var shipped = services.Last(entry => entry.ServiceType == typeof(IAlvoManagement) && !entry.IsKeyedService);
        services.Remove(shipped);
        services.Add(new ServiceDescriptor(
            typeof(IAlvoManagement),
            provider => new Holding(Build(shipped, provider), this),
            shipped.Lifetime));
    }

    /// <summary>Takes the armed hold, once: the apply that takes it is the only one held.</summary>
    private (TaskCompletionSource Written, TaskCompletionSource Release)? TakeHold()
    {
        lock (_gate)
        {
            if (_written is null || _release is null || _written.Task.IsCompleted)
            {
                return null;
            }

            return (_written, _release);
        }
    }

    private static IAlvoManagement Build(ServiceDescriptor shipped, IServiceProvider provider)
        => (IAlvoManagement)(shipped.ImplementationFactory?.Invoke(provider)
            ?? shipped.ImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, shipped.ImplementationType!));

    /// <summary>The shipped service, with one real apply held after it wrote.</summary>
    private sealed class Holding(IAlvoManagement inner, HeldApplyWorld world) : IAlvoManagement
    {
        public async Task<ManagementApplyResult> ApplyDescriptorAsync(
            string project, ManagementApplyRequest request, CancellationToken ct = default)
        {
            var result = await inner.ApplyDescriptorAsync(project, request, ct).ConfigureAwait(false);
            if (!request.DryRun && world.TakeHold() is { } hold)
            {
                hold.Written.TrySetResult();
                await hold.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return result;
        }

        public Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

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

        /// <inheritdoc/>
        public Task<ManagementCelFunctions> GetCelFunctionsAsync(string project, CancellationToken ct = default)
            => inner.GetCelFunctionsAsync(project, ct);

        public Task<ManagementPolicyVerdict> SimulatePolicyAsync(
            string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
            => inner.SimulatePolicyAsync(project, simulation, ct);

        public Task<ManagementExpressionVerdict> CheckExpressionAsync(
            string project, ManagementExpressionCheck request, CancellationToken ct = default)
            => inner.CheckExpressionAsync(project, request, ct);

        public Task<ManagementApplyResult> RollbackAsync(
            string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)
            => inner.RollbackAsync(project, targetRevision, request, ct);

        public Task SetAiConnectionAsync(StoredAiConnection connection, CancellationToken ct = default)
            => inner.SetAiConnectionAsync(connection, ct);
    }
}

/// <summary>
/// An apply that lands while the operator's other tab staged an edit, with somebody else applying in between: the
/// apply is confirmed, the copy keeps its old base, and the screen says so in place with the way out (spec §3.3).
/// </summary>
/// <remarks>
/// Before this, Preview said only "Applied as revision N" and drew the later edit as pending, and the next apply was
/// refused as a conflict with nothing on screen having warned it would be.
/// </remarks>
/// <param name="world">A host whose next apply can be held after it wrote.</param>
public sealed class KeptFollowScenarios(HeldApplyWorld world) : IClassFixture<HeldApplyWorld>
{
    /// <summary>
    /// Round one: Cancel keeps the edits and the warning, the copy is refused as a conflict, and only the confirm starts
    /// it again. Round two: the same overtaken apply, and another tab's Discard takes the warning away (the copy it
    /// described is gone).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_apply_overtaken_by_another_says_so_in_place_and_starts_again_only_through_its_confirm()
    {
        var cancel = TestContext.Current.CancellationToken;
        await using var applier = await world.SignInAsync(cancel);
        await using var otherTab = await world.SignInAsync(cancel);

        var kept = await OvertakeAnApplyAsync(applier, otherTab, "tickets", "parts");
        await CancelKeepsTheEditsAndTheConflictAsync(applier, kept);

        await applier.Page.GetByTestId("apply-kept-restart").ClickAsync();
        await applier.Dialog("restart-confirm").GetByTestId("restart-confirm-run").ClickAsync();
        await applier.SnackbarAsync("Started again from revision");
        await applier.FocusAfterConfirmAsync("restart-confirm", "#a-content");
        await kept.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await applier.Content.GetByText("Nothing to apply").WaitForAsync();

        kept = await OvertakeAnApplyAsync(applier, otherTab, "invoices", "vehicles");
        await otherTab.Page.GetByTestId("pending-discard").ClickAsync();
        await otherTab.Dialog("discard-sheet").GetByTestId("discard-confirm").ClickAsync();
        await kept.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        applier.AssertConsoleClean();
        otherTab.AssertConsoleClean();
    }

    /// <summary>
    /// Stages <paramref name="applied"/> and applies it, holding the apply while the other tab stages
    /// <paramref name="later"/> and somebody else applies; answers the warning that says so.
    /// </summary>
    private async Task<ILocator> OvertakeAnApplyAsync(AdminSession applier, AdminSession otherTab, string applied, string later)
    {
        await StageEntityAsync(applier, applied);
        await applier.PreviewPendingAsync();
        await applier.Page.FillAsync("#apply-reason", $"Add {applied}");

        world.HoldNextApply();
        await applier.Button("Apply these changes").ClickAsync();
        await world.Written.WaitAsync(TestContext.Current.CancellationToken);
        await StageEntityAsync(otherTab, later);
        await ApplyScenarioSteps.ApplySomebodyElsesChangeAsync(world, $"Changed behind the dashboard, after {applied}.");
        world.Release();

        var kept = applier.Page.GetByTestId("apply-kept");
        await kept.WaitForAsync();
        await applier.SnackbarAsync("Applied as revision");
        (await kept.InnerTextAsync()).ShouldContain("still lists the change you just applied as pending");
        await applier.WaitForFocusInsideAsync("apply-kept");
        return kept;
    }

    /// <summary>Cancel on the restart keeps everything; asking again is refused as the conflict the warning named.</summary>
    private static async Task CancelKeepsTheEditsAndTheConflictAsync(AdminSession applier, ILocator kept)
    {
        await applier.Page.GetByTestId("apply-kept-restart").ClickAsync();
        var confirm = applier.Dialog("restart-confirm");
        await confirm.GetByTestId("restart-confirm-cancel").ClickAsync();
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await kept.WaitForAsync();
        await applier.FocusAfterConfirmAsync("restart-confirm", "[data-testid='apply-kept-restart']");
        (await applier.Content.GetByText("Nothing to apply").CountAsync()).ShouldBe(0, "Cancel threw nothing away");

        await applier.Button("Plan this change").ClickAsync();
        (await applier.Page.GetByTestId("error-title").InnerTextAsync())
            .ShouldBe("Somebody applied a revision in between", "the kept copy is refused as the conflict it is");
        await kept.WaitForAsync();
    }

    /// <summary>Stages one new entity from the schema list.</summary>
    internal static async Task StageEntityAsync(AdminSession session, string name)
    {
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", name);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForAddressAsync($"**/schema/{name}");
    }
}
