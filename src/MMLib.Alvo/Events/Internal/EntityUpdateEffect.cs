using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The <c>entity.update</c> arm of <see cref="DescriptorActionRunner"/>: writes its target exactly once per event, in the event's
/// tenant, as the system — the Idempotent Consumer pattern the command path already uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly once through the command ledger.</b> The delivery claims the key <c>&lt;subscriber&gt;/&lt;event id&gt;</c> in scope
/// <c>alvo.action</c>, then writes and commits the key inside the write's own <see cref="IAlvoTransactions.TransactAsync"/> unit.
/// A retry — after a crash, a lost answer, an operator's redeliver — finds the key committed and writes nothing. The horizon is
/// the ledger's one-day sweep.
/// </para>
/// <para>
/// <b>In the event's tenant, fail closed.</b> The unit runs as the delivery's caller, the system in the event's tenant, so a
/// <c>recordId</c> of another tenant's row is not found and the delivery ends dead <c>record-not-found</c>; a tenant-scoped target
/// written from an event that carries no tenant ends the same way rather than as a tenantless system write. The write runs as the
/// system: it bypasses the target's rules, and its own event is one hop deeper, so the chain-depth limit stops a rule that
/// re-triggers itself.
/// </para>
/// </remarks>
/// <param name="ledger">The command ledger; absent outside a relational provider, where apply already refused the action.</param>
/// <param name="transactions">The transaction units; absent with the ledger, for the same reason.</param>
/// <param name="catalogs">The serving catalog, read for the target's tenancy and fields.</param>
/// <param name="values">Evaluates the payload over the event.</param>
/// <param name="time">The clock the key is claimed and committed on.</param>
internal sealed class EntityUpdateEffect(
    ICommandLedger? ledger, IAlvoTransactions? transactions, IPolicyCatalogProvider catalogs, ActionValues values, TimeProvider time)
{
    /// <summary>The ledger scope an <c>entity.update</c>'s keys are claimed in.</summary>
    internal const string Scope = "alvo.action";

    private static readonly CommandAnswer _applied = new(204, Body: null);

    /// <summary>Writes the action's target once for the delivery's event.</summary>
    /// <exception cref="ActionRefusedException">The record is not there, the write is refused, or the record id is not one.</exception>
    internal async Task ApplyAsync(CompiledAction action, AlvoEventContext context, CancellationToken ct)
    {
        var key = new CommandKey(Scope, $"{context.Subscriber}/{context.EventId:N}", context.Subscriber);
        var claim = await Ledger.ClaimAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (claim.State is CommandClaimState.Answered or CommandClaimState.Unanswered)
        {
            return;
        }

        if (claim.State is not CommandClaimState.Claimed)
        {
            throw new InvalidOperationException($"Another attempt of '{context.Subscriber}' holds event {context.EventId}; it is retried later.");
        }

        await WriteOnceAsync(action, context, key with { Token = claim.Token }, ct).ConfigureAwait(false);
    }

    private async Task WriteOnceAsync(CompiledAction action, AlvoEventContext context, CommandKey key, CancellationToken ct)
    {
        try
        {
            var write = Planned(action, context, ct);
            await Transactions.TransactAsync(context.Caller, (unit, token) => WriteAsync(unit, write, key, token), cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await Ledger.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw Classified(failure);
        }

        await Ledger.AnswerAsync(key, _applied, CancellationToken.None).ConfigureAwait(false);
    }

    private PlannedWrite Planned(CompiledAction action, AlvoEventContext context, CancellationToken ct)
    {
        var target = TargetOf(action.Target!);
        if (target.Tenancy == TenancyMode.Scoped && context.Caller.Tenant is null)
        {
            throw new ActionRefusedException(DeliveryErrors.RecordNotFound);
        }

        var recordId = RecordIdOf(action, context.Event);
        var written = Converted(values.Evaluate(action, context.Event, ct), target);
        EnsureEveryValueFits(written, target);
        if (recordId is null && target.Tenancy == TenancyMode.Scoped)
        {
            written[AlvoManagedColumns.TenantId] = context.Caller.Tenant!.Value.Value;
        }

        return new PlannedWrite(target.Name, recordId, written);
    }

    private async Task WriteAsync(IAlvoTransaction unit, PlannedWrite write, CommandKey key, CancellationToken ct)
    {
        if (write.RecordId is { } id)
        {
            await unit.UpdateAsync(write.Entity, id, write.Values, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await unit.CreateAsync(write.Entity, write.Values, ct).ConfigureAwait(false);
        }

        await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>The record the action names, rendered from the triggering event; <see langword="null"/> for a create.</summary>
    private static Guid? RecordIdOf(CompiledAction action, AlvoEvent @event) =>
        action.RecordId is not { } template ? null
        : Guid.TryParse(template.Render(@event), out var id) ? id
        : throw new ActionRefusedException(DeliveryErrors.ActionInputInvalid);

    /// <summary>Each value as the data port takes it: a literal converted to the field's type, an expression's value as it is.</summary>
    /// <summary>
    /// Refuses the write, before its unit opens, when a value breaks its target field's declared facets — Ruling Q22, the
    /// before-hook measure (Ruling V) applied to an expression's result. Dead <c>write-refused</c>: a retry evaluates the
    /// same row to the same value. The value is never put in the reason or a log.
    /// </summary>
    private static void EnsureEveryValueFits(Dictionary<string, object?> written, EntitySchema target)
    {
        foreach (var (name, value) in written)
        {
            var field = target.Fields.FirstOrDefault(candidate => candidate.Name == name);
            if (field is not null && PayloadFacets.TargetOf(field, target, descriptor: null).Violation(value) is not null)
            {
                throw new ActionRefusedException(DeliveryErrors.WriteRefused);
            }
        }
    }

    private static Dictionary<string, object?> Converted(Dictionary<string, object?> payload, EntitySchema target) =>
        payload.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is JsonElement literal ? Literal(literal, target, pair.Key) : pair.Value,
            StringComparer.Ordinal);

    private static object? Literal(JsonElement literal, EntitySchema target, string field) =>
        literal.ValueKind == JsonValueKind.Null
            ? null
            : FieldLiteral.Convert(literal, CelFieldType.Of(target.Fields.First(candidate => candidate.Name == field)));

    private EntitySchema TargetOf(string entity) =>
        catalogs.Current?.Schema.Entities.FirstOrDefault(candidate => candidate.Name == entity)
        ?? throw new ActionRefusedException(DeliveryErrors.RecordNotFound);

    /// <summary>What a failed write means for its delivery: a refusal no retry cures is dead; anything else is retried.</summary>
    private static Exception Classified(Exception failure) => failure switch
    {
        ActionRefusedException => failure,
        AlvoRecordNotFoundException => new ActionRefusedException(DeliveryErrors.RecordNotFound),
        AlvoConstraintViolationException or AlvoWriteRejectedException or AlvoAuthorizationException => new ActionRefusedException(DeliveryErrors.WriteRefused),
        CelFunctionException => new ActionRefusedException(DeliveryErrors.ActionInputInvalid),
        ArgumentException and not ArgumentNullException => new ActionRefusedException(DeliveryErrors.WriteRefused),
        _ => failure,
    };

    private ICommandLedger Ledger => ledger ?? throw Unavailable();

    private IAlvoTransactions Transactions => transactions ?? throw Unavailable();

    private static InvalidOperationException Unavailable() => new(
        "An 'entity.update' action reached a host that keeps no command ledger or transaction unit; the descriptor's apply "
        + "refuses that action there, so this is an invariant rather than a configuration mistake.");

    /// <summary>One write, planned before its unit opens: the entity, the record or none for a create, the values.</summary>
    private sealed record PlannedWrite(string Entity, Guid? RecordId, Dictionary<string, object?> Values);
}
