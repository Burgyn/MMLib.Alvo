namespace MMLib.Alvo.Samples.FieldService.Notifications;

/// <summary>Where this host's messages leave it: the customer, the on-call dispatcher and the dispatch desk.</summary>
/// <remarks>
/// A port, so the handlers that call it never know whether a message becomes an SMS, an e-mail or — in the demo stack — a
/// request in a WireMock journal. <see cref="HttpFieldServiceNotifier"/> is the one implementation this sample ships.
/// </remarks>
internal interface IFieldServiceNotifier
{
    /// <summary>Tells the customer when the technician is coming (<c>POST {url}/customers</c>).</summary>
    Task NotifyCustomerAsync(VisitNotice notice, CancellationToken cancellationToken);

    /// <summary>Pages the on-call dispatcher about an emergency nobody scheduled in time (<c>POST {url}/on-call</c>).</summary>
    Task PageOnCallAsync(SlaBreachNotice notice, CancellationToken cancellationToken);

    /// <summary>Sends one tenant's overdue report to its dispatch desk (<c>POST {url}/reports/overdue</c>).</summary>
    Task SendOverdueReportAsync(OverdueReport report, CancellationToken cancellationToken);
}

/// <summary>The customer's visit notice.</summary>
/// <param name="WorkOrderId">The job.</param>
/// <param name="Reference">The job's reference, as the customer's ticket names it.</param>
/// <param name="JobNumber">The job's number.</param>
/// <param name="ContactEmail">Who to tell.</param>
/// <param name="ScheduledFor">When the technician is due on site.</param>
internal sealed record VisitNotice(Guid WorkOrderId, string Reference, string? JobNumber, string? ContactEmail, DateTimeOffset? ScheduledFor);

/// <summary>The on-call page for an emergency still unscheduled at its respond-by time.</summary>
/// <param name="WorkOrderId">The job.</param>
/// <param name="Reference">The job's reference.</param>
/// <param name="JobNumber">The job's number.</param>
/// <param name="RespondBy">The deadline it missed.</param>
internal sealed record SlaBreachNotice(Guid WorkOrderId, string Reference, string? JobNumber, DateTimeOffset RespondBy);

/// <summary>One tenant's open jobs past their slot or their respond-by time, as of one morning.</summary>
/// <param name="Tenant">The tenant reported on.</param>
/// <param name="At">The occurrence the report was taken for.</param>
/// <param name="References">The overdue jobs' references, ordered.</param>
internal sealed record OverdueReport(Guid Tenant, DateTimeOffset At, IReadOnlyList<string> References);
