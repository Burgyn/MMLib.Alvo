using MMLib.Alvo.Samples.FieldService.Notifications;

namespace MMLib.Alvo.Samples.FieldService.Tests;

/// <summary>Every message the host's handlers and jobs sent, in order, instead of posting it anywhere.</summary>
internal sealed class RecordingNotifier : IFieldServiceNotifier
{
    private readonly Lock _gate = new();
    private readonly List<VisitNotice> _visits = [];
    private readonly List<SlaBreachNotice> _pages = [];
    private readonly List<OverdueReport> _reports = [];

    public IReadOnlyList<VisitNotice> Visits => Snapshot(_visits);

    public IReadOnlyList<SlaBreachNotice> Pages => Snapshot(_pages);

    public IReadOnlyList<OverdueReport> Reports => Snapshot(_reports);

    public Task NotifyCustomerAsync(VisitNotice notice, CancellationToken cancellationToken) => Record(_visits, notice);

    public Task PageOnCallAsync(SlaBreachNotice notice, CancellationToken cancellationToken) => Record(_pages, notice);

    public Task SendOverdueReportAsync(OverdueReport report, CancellationToken cancellationToken) => Record(_reports, report);

    private Task Record<T>(List<T> sent, T message)
    {
        lock (_gate)
        {
            sent.Add(message);
        }

        return Task.CompletedTask;
    }

    private List<T> Snapshot<T>(List<T> sent)
    {
        lock (_gate)
        {
            return [.. sent];
        }
    }
}
