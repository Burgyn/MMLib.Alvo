using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Scheduling;

/// <summary>A booked visit starts after the customer opens and ends by the hour they close, in the tenant's local time.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> as for <see cref="WithinServiceHours"/> — it reads two other rows (the visit's job, then the job's
/// customer) and compares local wall-clock hours in a named zone, which neither SQL-compiled rules nor Alvo's CEL can do.
/// </para>
/// <para>
/// The visit is the calendar, so this is the check that matters: it runs on every visit write — the <c>dispatch</c>
/// command's booking, and a dispatcher's own <c>POST</c> or <c>PATCH</c> of <c>/api/visits</c> — inside the write's
/// transaction. A slot that starts before opening is refused at <c>/starts_at</c>, one that ends after closing (or runs
/// past midnight) at <c>/ends_at</c>. An unresolvable job is left to the descriptor's own reference check.
/// </para>
/// </remarks>
internal sealed class VisitWithinServiceHours(IOptions<FieldServiceOptions> options) : IBeforeWriteHook<Visit>
{
    public const string Id = "visit-within-service-hours";

    public async ValueTask BeforeWriteAsync(BeforeWrite<Visit> write, CancellationToken cancellationToken)
    {
        if (KeepsItsSlot(write))
        {
            return;
        }

        var system = write.Transaction.AsSystem();
        var order = await system.GetAsync<WorkOrder>(write.New.WorkOrderId, cancellationToken);
        var customer = order is null ? null : await system.GetAsync<Customer>(order.CustomerId, cancellationToken);
        if (customer is not null
            && ServiceHours.Violation(
                customer.ServiceOpensHour, customer.ServiceClosesHour, write.New.StartsAt, write.New.EndsAt, options.Value.TimeZone) is { } field)
        {
            write.Invalid(ServiceHours.RefusalCode, ServiceHours.RefusalMessage, field);
        }
    }

    private static bool KeepsItsSlot(BeforeWrite<Visit> write) =>
        write.Old is { } old
        && old.StartsAt == write.New.StartsAt
        && old.EndsAt == write.New.EndsAt
        && old.WorkOrderId == write.New.WorkOrderId;
}
