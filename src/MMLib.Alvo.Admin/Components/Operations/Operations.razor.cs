using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>
/// The Operations page: handlers with their run counts and their pause, schedules and data migrations with their next and
/// last run, the timer counts, the delivery list, one delivery's attempts.
/// </summary>
public partial class Operations
{
    private readonly RefusalState<AdminProblem> _problem = new();
    private IReadOnlyList<ManagementExtension>? _handlers;
    private IReadOnlyList<ManagementExtension>? _jobs;
    private ManagementTimerCounts? _timers;
    private ManagementExtensionStats? _stats;
    private IReadOnlyList<ManagementDelivery>? _deliveries;
    private ManagementDelivery? _selected;
    private string? _fixture;
    private string? _status;
    private string? _reason;
    private readonly NestedBusy _busy = new();
    private OperationsLoader? _loader;
    private Question? _confirming;
    private string? _subscriberPrefix;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private bool Loading => _loader?.Loading ?? true;

    private DateTimeOffset Now => (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    /// <inheritdoc/>
    protected override Task OnInitializedAsync()
    {
        _loader = new OperationsLoader(Gateway);
        _subscriberPrefix = SubscriberPrefix(Navigation.Uri);
        return LoadAsync();
    }

    /// <summary>
    /// The deliveries the list draws: every loaded one, or — opened from the Automations page with <c>?subscriber=</c> — those
    /// whose subscriber starts with that prefix.
    /// </summary>
    /// <remarks>
    /// A descriptor action's subscriber id ends in a content hash the dashboard cannot compute, so the link carries the rule's
    /// prefix and the list filters the page it loaded (HX-15 ruling Q15: the smaller of the two options, no public surface).
    /// </remarks>
    private IReadOnlyList<ManagementDelivery>? ShownDeliveries => _subscriberPrefix is { } prefix && _deliveries is { } loaded
        ? [.. loaded.Where(delivery => delivery.Subscriber.StartsWith(prefix, StringComparison.Ordinal))]
        : _deliveries;

    private void ClearSubscriberFilter()
    {
        _subscriberPrefix = null;
        Navigation.NavigateTo(AdminPaths.Operations);
    }

    private static string? SubscriberPrefix(string uri) =>
        QueryHelpers.ParseQuery(new Uri(uri).Query).TryGetValue("subscriber", out var values) && values.ToString() is { Length: > 0 } prefix
            ? prefix
            : null;

    private ManagementExtensionStat? Stat(string id) => _stats?.Items.FirstOrDefault(item => item.Id == id);

    private AlvoButton.ButtonTone FilterTone(string? status) =>
        status == _status ? AlvoButton.ButtonTone.Primary : AlvoButton.ButtonTone.Secondary;

    private Task LoadAsync() => GuardedAsync(async () =>
    {
        if (await _loader!.LoadAsync(_status, _selected?.Id, default) is { } snapshot)
        {
            Show(snapshot);
        }
    });

    private void Show(OperationsSnapshot snapshot)
    {
        _handlers = snapshot.Handlers;
        _jobs = snapshot.Jobs;
        _timers = snapshot.Timers;
        _stats = snapshot.Stats;
        _deliveries = snapshot.Deliveries;
        _selected = snapshot.Selected;
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

    private void AskRunNow(string schedule) => _confirming = new Question(
        $"Run {schedule} now?",
        "One run starts now, outside its cron, and is recorded as a delivery like every other run. Its next occurrence stays where it was.",
        "Run now",
        async () =>
        {
            _fixture = null;
            _selected = await Gateway.RunScheduleAsync(schedule, default);
            Snackbar.Confirm($"{schedule} will run — its delivery is open beside the list.");
        },
        [TestIdSelector($"ops-run-now-{schedule}"), "#a-content"],
        AsksReason: false);

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
        if (_busy.Busy || _confirming is not { } question)
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
        using var running = _busy.Enter();
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
    }

    /// <summary>A change waiting for the operator's yes, and where focus goes once it ran (the trigger may be gone).</summary>
    private sealed record Question(string Title, string Consequence, string Verb, Func<Task> Run, IReadOnlyList<string> FocusAfter, bool AsksReason = true);
}
