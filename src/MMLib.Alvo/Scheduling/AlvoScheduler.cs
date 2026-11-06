using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The one <see cref="IAlvoScheduler"/>: it decides a timer's tenant, provenance and depth from the calling flow — never from
/// the caller — and arms it through <see cref="ITimerStore"/>, which joins the transaction unit open on the flow.
/// </summary>
/// <remarks>
/// The stored depth is the calling flow's effective depth; the timer pump fires the event one hop deeper. So a handler at
/// depth <c>d</c> (its event's depth + 1) arms a timer stored at <c>d</c>, whose event carries <c>d + 1</c> and whose handler
/// runs at <c>d + 2</c>. A refusal made while a unit is open poisons the unit, as every refusal inside one does.
/// </remarks>
/// <param name="store">Where timers are armed.</param>
/// <param name="catalogs">The applied schema, which says whether a timer outside a unit needs a tenant.</param>
/// <param name="depth">The chain-depth limit.</param>
/// <param name="time">The clock the message envelope is stamped with.</param>
internal sealed class AlvoScheduler(ITimerStore store, IPolicyCatalogProvider catalogs, AlvoChainDepthLimit depth, TimeProvider time)
    : IAlvoScheduler
{
    /// <inheritdoc/>
    public async Task ScheduleAsync(
        string key,
        DateTimeOffset at,
        string eventType,
        IReadOnlyDictionary<string, object?>? data,
        TenantId? tenant = null,
        CancellationToken cancellationToken = default) =>
        await store.UpsertAsync(Refused(() => TimerOf(key, at, eventType, data, tenant)), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<bool> CancelAsync(string key, TenantId? tenant = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var scope = Refused(() => ScopeOf(tenant));

        return await store.CancelAsync(scope?.ToString() ?? string.Empty, key, cancellationToken).ConfigureAwait(false);
    }

    private TimerEntry TimerOf(string key, DateTimeOffset at, string eventType, IReadOnlyDictionary<string, object?>? data, TenantId? tenant)
    {
        ArgumentNullException.ThrowIfNull(key);
        var subject = $"timers/{key}";
        AlvoCustomEnvelope.Ensure(eventType, subject, data);
        var context = AlvoContext.System(ScopeOf(tenant));
        depth.EnsureWithin(context);
        var message = AlvoCustomEnvelope.Build(eventType, subject, data, context, time.GetUtcNow());

        return TimerEntry.Of(context.Tenant?.ToString() ?? string.Empty, key, at, message, StoredProvenance(context, message.Envelope));
    }

    /// <summary>
    /// The calling flow's effective provenance, with the correlation and trace context the message envelope settled on — so a
    /// timer armed under a W3C trace outside any flow belongs to that trace, as every other emit does.
    /// </summary>
    private static AlvoProvenance StoredProvenance(AlvoContext context, AlvoEvent envelope) =>
        AlvoEventProvenance.Effective(context) with
        {
            CorrelationId = envelope.CorrelationId,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
        };

    private TenantId? ScopeOf(TenantId? tenant)
    {
        var unitTenant = AlvoFlow.Unit?.Caller.Tenant;
        if (tenant is { } stated && unitTenant is { } open && stated != open)
        {
            throw TenantMismatch(stated, open);
        }

        return tenant ?? unitTenant ?? (TenancyOn() ? throw TenantRequired() : null);
    }

    private bool TenancyOn() =>
        catalogs.Current is { } catalog
            ? catalog.Schema.Entities.Any(entity => entity.Tenancy == TenancyMode.Scoped)
            : throw Unprimed();

    private static T Refused<T>(Func<T> decide)
    {
        try
        {
            return decide();
        }
        catch (Exception refusal) when (AlvoFlow.Unit is { } unit)
        {
            unit.Poison(refusal);
            throw;
        }
    }

#pragma warning disable CA2208 // "tenant" is the IAlvoScheduler parameter the caller handed in.
    private static ArgumentException TenantMismatch(TenantId stated, TenantId open) => new(
        $"A timer was armed for tenant {stated} inside a transaction unit opened for tenant {open}, so it was refused and the "
        + "unit will roll back. Inside a unit a timer belongs to the unit's tenant. Fix: drop the tenant: argument, or arm the "
        + "timer outside the unit.",
        "tenant");

    private static ArgumentException TenantRequired() => new(
        "A timer was armed with no tenant outside any transaction unit, and the schema has a tenant-scoped entity, so it was "
        + "refused. Fix: pass tenant: — the tenant the timer's event belongs to (in a handler, the event's tenant).",
        "tenant");
#pragma warning restore CA2208

    private static InvalidOperationException Unprimed() => new(
        "A timer was armed with no tenant before the schema was primed, so whether it needs one is unknown and it was refused. "
        + "Fix: arm it after the host has started, or pass tenant: — the tenant the timer's event belongs to.");
}
