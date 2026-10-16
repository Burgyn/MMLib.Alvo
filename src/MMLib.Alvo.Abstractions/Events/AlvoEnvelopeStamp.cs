namespace MMLib.Alvo.Events;

/// <summary>
/// Every provenance attribute one envelope carries, derived from the caller once — the single place both emit paths
/// (the EF driver's data events, the core's custom events) turn an <see cref="AlvoContext"/> into envelope attributes.
/// </summary>
/// <remarks>
/// Internal: two copies of this derivation are how a system-made change comes to be reported differently on one path
/// than on the other, which is the reason <see cref="AlvoEventProvenance"/> exists at all.
/// </remarks>
internal sealed record AlvoEnvelopeStamp(
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
        var (traceParent, traceState) = TraceContextOf(provenance);

        return new AlvoEnvelopeStamp(
            AlvoEventProvenance.AuthTypeOf(context),
            AlvoEventProvenance.AuthIdOf(context),
            AlvoEventProvenance.AuthClaimsOf(context),
            tenant?.ToString(),
            provenance.CorrelationId
                ?? W3CTraceContext.TraceIdOf(traceParent)
                ?? AlvoEventProvenance.CorrelationIdOf(eventId),
            provenance.CausationId,
            provenance.Depth,
            traceParent,
            traceState);
    }

    /// <summary>
    /// The trace context the envelope carries: the stated one, else the ambient W3C activity's — each kept only when
    /// it is valid W3C, so the correlation id and the trace context name one flow, never two.
    /// </summary>
    /// <remarks>
    /// An invalid <c>traceparent</c> takes its <c>tracestate</c> with it, as W3C requires of a receiver; an invalid
    /// <c>tracestate</c> under a valid parent is dropped alone.
    /// </remarks>
    private static (string? TraceParent, string? TraceState) TraceContextOf(AlvoProvenance provenance)
    {
        var activity = AlvoEventProvenance.AmbientW3CActivity;
        var fromActivity = provenance.TraceParent is null && activity is not null;
        var traceParent = fromActivity ? activity!.Id : provenance.TraceParent;
        var traceState = fromActivity ? activity!.TraceStateString : provenance.TraceState;

        return traceParent is null || !W3CTraceContext.IsTraceParent(traceParent)
            ? (null, null)
            : (traceParent, string.IsNullOrEmpty(traceState) || !W3CTraceContext.IsTraceState(traceState) ? null : traceState);
    }

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
