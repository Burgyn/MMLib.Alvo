using System.Net.Http.Json;

namespace MMLib.Alvo.Samples.FieldService.Notifications;

/// <summary>Posts each message as JSON under <see cref="FieldServiceOptions.NotificationsUrl"/>.</summary>
/// <remarks>
/// A failed post throws, and that is the point: every caller is an event handler or a scheduled job, which Alvo retries
/// with back-off and finally dead-letters on <em>Operations</em>. Swallowing the failure here would lose the message
/// silently. In the demo stack the URL is a WireMock sidecar; in production, the SMS or e-mail gateway.
/// </remarks>
internal sealed class HttpFieldServiceNotifier(HttpClient http) : IFieldServiceNotifier
{
    public Task PageOnCallAsync(SlaBreachNotice notice, CancellationToken cancellationToken) =>
        PostAsync("on-call", notice, cancellationToken);

    public Task SendOverdueReportAsync(OverdueReport report, CancellationToken cancellationToken) =>
        PostAsync("reports/overdue", report, cancellationToken);

    private async Task PostAsync<T>(string path, T message, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
