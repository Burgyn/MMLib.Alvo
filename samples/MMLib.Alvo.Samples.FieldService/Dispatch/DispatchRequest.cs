using MMLib.Alvo.Api;
using System.ComponentModel;

namespace MMLib.Alvo.Samples.FieldService.Dispatch;

/// <summary>What the dispatcher sends to book a job: who goes, when, and for how long.</summary>
/// <remarks>
/// Every member is nullable so a missing one is told apart from a default one: minimal-API binding does not enforce a
/// record's constructor parameters, and a missing technician would otherwise arrive as <see cref="Guid.Empty"/>.
/// </remarks>
/// <param name="TechnicianId">The user id of the technician booked.</param>
/// <param name="StartsAt">When the technician is due on site.</param>
/// <param name="DurationMinutes">How long the visit is booked for.</param>
[Description("Assign a technician to a new job and book the visit in the calendar, in one step.")]
internal sealed record DispatchRequest(
    [property: Description("The user id of the technician booked. Required.")] Guid? TechnicianId,
    [property: Description("When the technician is due on site. Required.")] DateTimeOffset? StartsAt,
    [property: Description("How long the visit is booked for, in minutes: 15 to 720. Required.")] int? DurationMinutes)
{
    public const int ShortestVisit = 15;

    public const int LongestVisit = 720;

    /// <summary>Every field the request leaves missing or out of range, at the pointer the caller sent it under.</summary>
    public IReadOnlyList<AlvoViolation> Violations()
    {
        List<AlvoViolation> violations = [];
        if (TechnicianId is null || TechnicianId == Guid.Empty)
        {
            violations.Add(FieldServiceProblems.Missing("/technicianId"));
        }

        if (StartsAt is null)
        {
            violations.Add(FieldServiceProblems.Missing("/startsAt"));
        }

        if (DurationMinutes is not { } minutes)
        {
            violations.Add(FieldServiceProblems.Missing("/durationMinutes"));
        }
        else if (minutes is < ShortestVisit or > LongestVisit)
        {
            violations.Add(FieldServiceProblems.Range("/durationMinutes", $"A visit is booked for {ShortestVisit} to {LongestVisit} minutes."));
        }

        return violations;
    }
}

/// <summary>What a dispatch answers: the job, the visit it booked and the job's number.</summary>
/// <param name="WorkOrder">The job dispatched.</param>
/// <param name="Visit">The visit booked for it.</param>
/// <param name="JobNumber">The job's number, as billing and the customer quote it.</param>
[Description("The dispatched job, the visit booked for it and the job's number.")]
internal sealed record DispatchResult(
    [property: Description("The job dispatched.")] Guid WorkOrder,
    [property: Description("The visit booked for it.")] Guid Visit,
    [property: Description("The job's number, as billing and the customer quote it.")] string? JobNumber);
