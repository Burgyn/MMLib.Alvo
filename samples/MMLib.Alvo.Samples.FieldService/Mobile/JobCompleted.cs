using System.Text.Json;

namespace MMLib.Alvo.Samples.FieldService.Mobile;

/// <summary>What the technician app posts when a job is signed off on site.</summary>
/// <param name="WorkOrderId">The job signed off.</param>
/// <param name="TenantId">The tenant the job belongs to — signed, so it is the only place the tenant may come from.</param>
/// <param name="CompletedOn">The day the job was signed off.</param>
/// <param name="MinutesOnSite">How long the technician was on site.</param>
internal sealed record JobCompleted(Guid WorkOrderId, Guid TenantId, DateOnly CompletedOn, int MinutesOnSite)
{
    /// <summary>The notice in a verified body, or <see langword="null"/> when the body is not one.</summary>
    /// <param name="body">The body bytes the signature was verified over.</param>
    public static JobCompleted? TryRead(ReadOnlyMemory<byte> body)
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
