using System.Text.RegularExpressions;

namespace MMLib.Alvo.Events;

/// <summary>
/// Every provenance attribute one envelope carries, derived from the caller once — the single place both emit paths
/// (the EF driver's data events, the core's custom events) turn an <see cref="AlvoContext"/> into envelope attributes.
/// </summary>
/// <remarks>
/// Internal: two copies of this derivation are how a system-made change comes to be reported differently on one path
/// than on the other, which is the reason <see cref="AlvoEventProvenance"/> exists at all.
/// </remarks>
internal sealed partial record AlvoEnvelopeStamp(
    string AuthType,
    string? AuthId,
    string? AuthClaims,
    string? TenantId,
    string CorrelationId,
    string? CausationId,
    int ChainDepth,
    string? TraceParent,
    string? TraceState)
{
    /// <summary>The stamp for one event emitted as <paramref name="context"/>.</summary>
    /// <param name="context">The caller the event is emitted for.</param>
    /// <param name="eventId">The event's own id, the correlation of last resort.</param>
    /// <param name="tenant">The tenant the event reports, or <see langword="null"/> for a global row or a tenantless write.</param>
    internal static AlvoEnvelopeStamp Of(AlvoContext context, Guid eventId, TenantId? tenant)
    {
        var provenance = AlvoEventProvenance.Effective(context);
        var activity = AlvoEventProvenance.AmbientW3CActivity;
        var fromActivity = provenance.TraceParent is null && activity is not null;
        var traceParent = fromActivity ? activity!.Id : provenance.TraceParent;

        return new AlvoEnvelopeStamp(
            AlvoEventProvenance.AuthTypeOf(context),
            AlvoEventProvenance.AuthIdOf(context),
            AlvoEventProvenance.AuthClaimsOf(context),
            tenant?.ToString(),
            provenance.CorrelationId ?? TraceIdOf(traceParent) ?? AlvoEventProvenance.CorrelationIdOf(eventId),
            provenance.CausationId,
            provenance.Depth,
            traceParent,
            fromActivity ? activity!.TraceStateString : provenance.TraceState);
    }

    /// <summary>
    /// The trace-id segment of a valid W3C <c>traceparent</c>, or <see langword="null"/> when there is none — so the
    /// correlation id and the trace context an envelope carries name one flow, never two.
    /// </summary>
    private static string? TraceIdOf(string? traceParent) =>
        traceParent is not null
        && TraceParentRegex().Match(traceParent) is { Success: true } match
        && match.Groups[TraceIdGroup].Value != InvalidTraceId
            ? match.Groups[TraceIdGroup].Value
            : null;

    private const string TraceIdGroup = "traceid";

    private const string InvalidTraceId = "00000000000000000000000000000000";

    [GeneratedRegex("^00-(?<traceid>[0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceParentRegex();

    /// <summary><paramref name="event"/> with every attribute of this stamp that is not a required member.</summary>
    internal AlvoEvent Onto(AlvoEvent @event) => @event with
    {
        AuthId = AuthId,
        AuthClaims = AuthClaims,
        TenantId = TenantId,
        CausationId = CausationId,
        ChainDepth = ChainDepth,
        TraceParent = TraceParent,
        TraceState = TraceState,
    };
}
