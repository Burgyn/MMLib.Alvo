using MMLib.Alvo.Api;
using System.Text.Json;

namespace MMLib.Alvo.Samples.FieldService.Mobile;

/// <summary>What the technician app posts when a job is signed off on site.</summary>
/// <remarks>
/// Every member is nullable so a missing one is refused rather than read as a default: an empty tenant id would otherwise
/// reach the tenant scope as the all-zero tenant, and a missing day would complete the job on 0001-01-01.
/// </remarks>
/// <param name="WorkOrderId">The job signed off.</param>
/// <param name="TenantId">The tenant the job belongs to — signed, so it is the only place the tenant may come from.</param>
/// <param name="CompletedOn">The day the job was signed off.</param>
/// <param name="MinutesOnSite">How long the technician was on site, 0 to 1440.</param>
internal sealed record JobCompleted(Guid? WorkOrderId, Guid? TenantId, DateOnly? CompletedOn, int? MinutesOnSite)
{
    private const int MinutesPerDay = 1440;

    /// <summary>The notice's tenant, or <see langword="null"/> — which fails closed — when the body names none.</summary>
    /// <param name="body">The body bytes the signature was verified over.</param>
    public static TenantId? TenantOf(ReadOnlyMemory<byte> body) =>
        TryRead(body)?.TenantId is { } tenant && tenant != Guid.Empty ? new TenantId(tenant) : null;

    /// <summary>Every field the notice leaves missing or out of range.</summary>
    public IReadOnlyList<AlvoViolation> Violations()
    {
        List<AlvoViolation> violations = [];
        if (WorkOrderId is null || WorkOrderId == Guid.Empty)
        {
            violations.Add(FieldServiceProblems.Missing("/workOrderId"));
        }

        if (TenantId is null || TenantId == Guid.Empty)
        {
            violations.Add(FieldServiceProblems.Missing("/tenantId"));
        }

        if (CompletedOn is null)
        {
            violations.Add(FieldServiceProblems.Missing("/completedOn"));
        }

        if (MinutesOnSite is not { } minutes)
        {
            violations.Add(FieldServiceProblems.Missing("/minutesOnSite"));
        }
        else if (minutes is < 0 or > MinutesPerDay)
        {
            violations.Add(FieldServiceProblems.Range("/minutesOnSite", $"Minutes on site are 0 to {MinutesPerDay}."));
        }

        return violations;
    }

    private static JobCompleted? TryRead(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<JobCompleted>(body.Span, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
