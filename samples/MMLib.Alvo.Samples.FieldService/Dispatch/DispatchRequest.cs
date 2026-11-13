using System.ComponentModel;

namespace MMLib.Alvo.Samples.FieldService.Dispatch;

/// <summary>What the dispatcher sends to book a job: who goes, when, and for how long.</summary>
/// <param name="TechnicianId">The user id of the technician booked.</param>
/// <param name="StartsAt">When the technician is due on site.</param>
/// <param name="DurationMinutes">How long the visit is booked for.</param>
[Description("Assign a technician to a new job and book the visit in the calendar, in one step.")]
internal sealed record DispatchRequest(
    [property: Description("The user id of the technician booked.")] Guid TechnicianId,
    [property: Description("When the technician is due on site.")] DateTimeOffset StartsAt,
    [property: Description("How long the visit is booked for, in minutes.")] int DurationMinutes);

/// <summary>What a dispatch answers: the job, the visit it booked and the job's number.</summary>
/// <param name="WorkOrder">The job dispatched.</param>
/// <param name="Visit">The visit booked for it.</param>
/// <param name="JobNumber">The job's number, as billing and the customer quote it.</param>
[Description("The dispatched job, the visit booked for it and the job's number.")]
internal sealed record DispatchResult(
    [property: Description("The job dispatched.")] Guid WorkOrder,
    [property: Description("The visit booked for it.")] Guid Visit,
    [property: Description("The job's number, as billing and the customer quote it.")] string? JobNumber);
