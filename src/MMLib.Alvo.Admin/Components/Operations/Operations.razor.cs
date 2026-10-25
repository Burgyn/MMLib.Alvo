using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>The Operations page: handlers with their run counts and their pause, the delivery list, one delivery's attempts.</summary>
public partial class Operations
{
    private readonly RefusalState<AdminProblem> _problem = new();
    private IReadOnlyList<ManagementExtension>? _handlers;
    private ManagementExtensionStats? _stats;
    private IReadOnlyList<ManagementDelivery>? _deliveries;
    private ManagementDelivery? _selected;
    private string? _fixture;
    private string? _status;
    private string? _reason;
    private Question? _confirming;
    private bool _loading;
    private bool _busy;

    /// <inheritdoc/>
    protected override Task OnInitializedAsync() => LoadAsync();

    private ManagementExtensionStat? Stat(string id) => _stats?.Items.FirstOrDefault(item => item.Id == id);

    private AlvoButton.ButtonTone FilterTone(string? status) =>
        status == _status ? AlvoButton.ButtonTone.Primary : AlvoButton.ButtonTone.Secondary;

    private async Task LoadAsync()
    {
        _loading = true;
        await GuardedAsync(async () =>
        {
            _handlers = await Gateway.HandlersAsync(default);
            _stats = await Gateway.StatsAsync(default);
            _deliveries = (await Gateway.DeliveriesAsync(_status, default)).Items;
            _selected = _selected is null ? null : await Gateway.DeliveryAsync(_selected.Id, default);
        });
        _loading = false;
    }

    private Task FilterAsync(string? status)
    {
        _status = status;
        return LoadAsync();
    }

    private Task OpenAsync(Guid delivery) => GuardedAsync(async () =>
    {
        _fixture = null;
        _selected = await Gateway.DeliveryAsync(delivery, default);
    });

    private void AskPause(string subscriber)
    {
        _reason = null;
        _confirming = new Question(
            $"Pause {subscriber}?",
            "Its deliveries keep being recorded and wait. Nothing new runs on any instance until you resume it; then they run in order.",
            "Pause",
            () => Gateway.SetPausedAsync(subscriber, paused: true, _reason, default),
            [TestIdSelector($"ops-resume-{subscriber}"), "#a-content"]);
    }

    private Task ResumeAsync(string subscriber) => GuardedAsync(async () =>
    {
        await Gateway.SetPausedAsync(subscriber, paused: false, reason: null, default);
        await LoadAsync();
        await Interop.FocusFirstOnceShownAsync([TestIdSelector($"ops-pause-{subscriber}")]);
    });

    private Task PlanReplayAsync(string subscriber) => GuardedAsync(async () =>
    {
        var plan = await Gateway.PlanReplayAsync(subscriber, default);
        var key = Guid.NewGuid().ToString("N");
        _reason = null;
        _confirming = plan.Count == 0
            ? null
            : new Question(
                OperationsView.ReplayQuestion(plan, subscriber),
                "They run again in the order of their events, after newer events of the same records that already ran. Their conditions are not evaluated again.",
                $"Replay {plan.Count}",
                () => Gateway.ReplayAsync(subscriber, _reason, key, default),
                [TestIdSelector($"ops-replay-{subscriber}"), "#a-content"]);
        if (plan.Count == 0)
        {
            _problem.Show(AdminProblem.From(new ManagementConflictException($"{subscriber} has no dead deliveries to run again.")), fromPress: true);
        }
    });

    private Task RedeliverAsync(Guid delivery) => GuardedAsync(async () =>
    {
        _selected = await Gateway.RedeliverAsync(delivery, reason: null, default);
        await LoadAsync();
    });

    private Task FixtureAsync(Guid delivery) => GuardedAsync(async () =>
        _fixture = OperationsView.FixtureJson(await Gateway.FixtureAsync(delivery, default)));

    private async Task ConfirmedAsync()
    {
        if (_busy || _confirming is not { } question)
        {
            return;
        }

        await GuardedAsync(async () =>
        {
            await question.Run();
            await LoadAsync();
            await Interop.FocusFirstOnceClosedAsync(question.FocusAfter);
            _confirming = null;
        });
    }

    private static string TestIdSelector(string testId) => $"[data-testid='{testId}']";

    private async Task GuardedAsync(Func<Task> step)
    {
        _busy = true;
        try
        {
            _problem.Clear();
            await step();
        }
        catch (Exception exception) when (AdminProblem.From(exception, Logger) is { } problem)
        {
            _confirming = null;
            _problem.Show(problem, fromPress: true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A change waiting for the operator's yes, and where focus goes once it ran (the trigger may be gone).</summary>
    private sealed record Question(string Title, string Consequence, string Verb, Func<Task> Run, IReadOnlyList<string> FocusAfter);
}
