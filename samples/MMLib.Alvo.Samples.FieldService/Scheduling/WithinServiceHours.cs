using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Scheduling;

/// <summary>A job is scheduled only inside its customer's service hours, read in the tenant's local time zone.</summary>
/// <remarks>
/// <para>
/// <b>Why C#:</b> the rule reads <em>another row</em> (the customer's hours) and converts the slot into a named time zone.
/// A descriptor rule, CHECK or lookup check compiles to SQL and has no time-zone conversion, and a before-hook's CEL
/// condition cannot read the customer. The customer's own hours are still guarded by the descriptor's CHECKs (0–23, close
/// after open); only the comparison across the two rows is code.
/// </para>
/// <para>
/// A before-write hook runs inside the write's transaction — the Data API's <c>PATCH</c> and the <c>dispatch</c> command's
/// unit alike — so a refused slot leaves nothing behind. Only a write that sets or moves <c>scheduled_for</c> is judged:
/// a slot booked before the customer changed their hours stays as it is for every other edit.
/// </para>
/// </remarks>
internal sealed class WithinServiceHours(IOptions<FieldServiceOptions> options) : IBeforeWriteHook<WorkOrder>
{
    public const string Id = "within-service-hours";

    public const string RefusalCode = "outside-service-hours";

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
            write.Invalid(RefusalCode, "The customer does not accept visits at that time.", "scheduled_for");
        }
    }
}
