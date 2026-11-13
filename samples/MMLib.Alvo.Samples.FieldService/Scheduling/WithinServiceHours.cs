using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Scheduling;

/// <summary>A job's planned start falls inside its customer's service hours, read in the tenant's local time zone.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> the rule reads <em>another row</em> (the customer's hours) and needs the slot's hour <em>in a named time
/// zone</em>. A descriptor rule, CHECK or lookup check compiles to SQL, and Alvo's CEL has no time-zone-aware hour
/// function, so no descriptor construct can say it. The hours themselves stay guarded by the descriptor's CHECKs (0–23,
/// close after open); only the comparison across the two rows is code.
/// </para>
/// <para>
/// A job's <c>scheduled_for</c> is a start with no end, so this checks the start only; the booked visit carries the whole
/// slot and <see cref="VisitWithinServiceHours"/> checks that it also ends in time. Only a write that sets or moves
/// <c>scheduled_for</c> is judged, so a job booked before the customer changed their hours stays editable. The customer is
/// read with <c>AsSystem()</c> because a technician may update their own job but not read <c>customers</c>.
/// </para>
/// </remarks>
internal sealed class WithinServiceHours(IOptions<FieldServiceOptions> options) : IBeforeWriteHook<WorkOrder>
{
    public const string Id = "within-service-hours";

    public async ValueTask BeforeWriteAsync(BeforeWrite<WorkOrder> write, CancellationToken cancellationToken)
    {
        if (write.New.ScheduledFor is not { } at || write.Old?.ScheduledFor == at)
        {
            return;
        }

        var customer = await write.Transaction.AsSystem().GetAsync<Customer>(write.New.CustomerId, cancellationToken);
        if (customer is not null
            && !ServiceHours.Admit(customer.ServiceOpensHour, customer.ServiceClosesHour, at, options.Value.TimeZone))
        {
            write.Invalid(ServiceHours.RefusalCode, ServiceHours.RefusalMessage, "scheduled_for");
        }
    }
}
