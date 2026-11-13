namespace MMLib.Alvo.Samples.FieldService;

/// <summary>The host's own settings, bound from the <c>FieldService</c> configuration section.</summary>
/// <remarks>
/// Everything here is configuration a real deployment changes — the descriptor's location, the tenant's local time zone,
/// where notifications go, when the overdue report runs and which tenants exist. None of it is a test switch: the
/// end-to-end suite drives the same values a production host would carry (spec ruling R8).
/// </remarks>
public sealed class FieldServiceOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "FieldService";

    /// <summary>
    /// Where the descriptor lives. Empty means the repository's <c>examples/field-service/field-service.alvo.json</c>, found
    /// above the content root — the same file the standalone stack mounts.
    /// </summary>
    public string DescriptorPath { get; set; } = "";

    /// <summary>The IANA zone a customer's service hours are read in.</summary>
    public string TimeZone { get; set; } = "Europe/Bratislava";

    /// <summary>
    /// The base URL notifications are posted to: the customer's visit notice, the on-call page and the overdue report. In
    /// the demo stack it is a WireMock sidecar; a real deployment points it at its SMS or e-mail gateway.
    /// </summary>
    public string NotificationsUrl { get; set; } = "";

    /// <summary>When the overdue report runs, as a five-field cron read in <see cref="TimeZone"/>.</summary>
    public string OverdueReportCron { get; set; } = "0 6 * * *";

    /// <summary>
    /// The tenants this host serves. Alvo does not know a host's tenants; a per-tenant schedule runs once for each one
    /// listed here.
    /// </summary>
    public IList<Guid> Tenants { get; } = [];
}
