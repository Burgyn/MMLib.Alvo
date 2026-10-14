using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Which compiled after-hooks one event is subscribed to: the entity and operation its <c>type</c> names, and
/// then the hooks whose condition holds for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The condition is part of the subscription, not the run's first step.</b> A dispatcher that ran the
/// action and evaluated the condition inside it would write an execution-log entry for every event and abort
/// almost all of them — the documented Directus defect §3.3 cites, where thousands of log rows describe runs
/// that stopped on their first condition. Alvo has the advantage by construction: the CEL
/// <see cref="CelProfile.Condition"/> expression was compiled when the descriptor was applied, so the
/// predicate is available <em>here</em>, and a filtered event costs one counter increment and no log entry.
/// </para>
/// <para>
/// <b>The entity and operation are read out of the event's <c>type</c>, which is the wire's own vocabulary.</b>
/// The driver that emits an event spells that type (<c>entity.{entity}.{created|updated|deleted}</c>) and this
/// is where it is read back; the two are separate assemblies — the emitting factory is
/// <see langword="internal"/> to the EF driver and the suffix vocabulary cannot be shared without widening a
/// port — so the pairing is held by the end-to-end criteria that drive a real write through a real dispatcher,
/// not by a shared constant. A type this cannot parse selects <b>nothing</b>, which is the fail-closed
/// direction: an unrecognised type is a queue entry from a build that spoke a different grammar, and running
/// every hook on it would be strictly worse than running none.
/// </para>
/// <para>
/// <b>A condition's caller is the caller <em>as at the event</em>, answered from the envelope and never from
/// whoever is dispatching.</b> It is built per event: <c>@user.id</c> from <see cref="AlvoEvent.AuthId"/>, the
/// credential that made the change; <c>@tenant.id</c> from <see cref="AlvoEvent.TenantId"/>, the tenant it wrote
/// in; <c>@user.roles</c> from <see cref="AlvoEvent.AuthClaims"/>, the roles it held then (#153). So
/// <c>new.owner_id != @user.id</c> ("don't notify whoever just changed it") compares two real values instead of
/// comparing a row against the framework's own reserved id, which is what a shared <c>AlvoContext.System</c> made
/// it do — and <c>'admin' in @user.roles</c> asks about the writer rather than the dispatcher's own
/// <see cref="Role.Admin"/>.
/// </para>
/// <para>
/// <b>An event that does not carry what a condition reads selects no hook that reads it.</b> An anonymous write
/// carries no <c>authid</c>; a global row, a tenantless system write and an outbox row written before HX-01 carry
/// no <c>tenantid</c>; a pre-HX-01 row carries no <c>authclaims</c>, and a malformed one reads as absent. Each of
/// those gates the hook out (<see cref="CompiledAfterHook.Required"/>, <see cref="CompiledAfterHook.ReadsRoles"/>)
/// instead of letting the comparison run against an absent operand, which a negation would turn into "every
/// event". Exactly the <see cref="RequiredContext"/> gate the policy engine applies to a rule, in the same
/// direction: refuse upstream rather than fold an absent operand into a verdict.
/// </para>
/// </remarks>
internal static class EventSubscriptions
{
    /// <summary>The hooks <paramref name="event"/> is subscribed to, in declaration order.</summary>
    /// <param name="catalog">The primed policy catalog the hooks were compiled into.</param>
    /// <param name="event">The event, as the outbox stored it.</param>
    /// <param name="evaluator">The evaluator every condition is judged by.</param>
    /// <param name="logger">Where a condition that threw, or one reading a value the event lacks, is recorded at Debug.</param>
    /// <returns>The matching hooks; empty when the event matched nothing at all.</returns>
    internal static IReadOnlyList<CompiledAfterHook> Matching(
        PolicyCatalog catalog,
        AlvoEvent @event,
        IPredicateEvaluator evaluator,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (!TryReadSubscription(@event.Type, out var entity, out var operation)
            || !catalog.TryGetEntity(entity, out var policy))
        {
            return [];
        }

        var caller = CallerOf(@event);

        return [.. policy.AfterHooks.For(operation).Where(hook => Selects(hook, @event, evaluator, caller, logger))];
    }

    /// <summary>
    /// The caller one event's conditions resolve against: the credential, tenant and roles the envelope records.
    /// </summary>
    /// <remarks>
    /// What the envelope does not record is left absent — the all-zero id, no tenant, only <see cref="Role.Anon"/> —
    /// and never borrowed from the dispatcher's own <see cref="AlvoContext.System"/> identity. <see cref="Selects"/>
    /// is what keeps a condition reading an absent value from being decided against it.
    /// </remarks>
    private static AlvoContext CallerOf(AlvoEvent @event)
    {
        var actor = UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var user) ? user : default;
        TenantId? tenant = TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return new AlvoContext { User = actor, Roles = RolesOf(@event), Tenant = tenant };
    }

    /// <summary>
    /// The roles <see cref="AlvoEvent.AuthClaims"/> records, or only <see cref="Role.Anon"/> when it records none —
    /// a caller always holds one, and a hook that reads roles off an envelope without readable claims is never
    /// selected (<see cref="UnreadProvenance"/>).
    /// </summary>
    private static IReadOnlySet<Role> RolesOf(AlvoEvent @event) =>
        AlvoEventProvenance.RolesIn(@event.AuthClaims) is { Count: > 0 } names
            ? RoleCatalog.Create(names).Resolve(names)
            : _noRoles;

    private static readonly IReadOnlySet<Role> _noRoles = new HashSet<Role> { Role.Anon };

    /// <summary>
    /// The reference <paramref name="hook"/>'s condition reads and <paramref name="event"/> does not carry, or
    /// <see langword="null"/> when it carries everything the condition reads.
    /// </summary>
    private static string? UnreadProvenance(CompiledAfterHook hook, AlvoEvent @event, AlvoContext caller) =>
        hook.Required.TenantIdMissingFrom(caller) ? TenantReference
        : hook.ReadsRoles && AlvoEventProvenance.RolesIn(@event.AuthClaims) is null ? RolesReference
        : null;

    private const string TenantReference = "@tenant.id";
    private const string RolesReference = "@user.roles";

    /// <summary>
    /// Whether <paramref name="hook"/>'s condition holds for <paramref name="event"/>. A hook declaring none
    /// always holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CEL function that fails is the one reachable throw: it drops the hook like any other, but at Warning with
    /// the function's name (<see cref="EventLog.ConditionFunctionFailed"/>), because the dropped hook may be an
    /// audit webhook.
    /// </para>
    /// <para>
    /// Any other condition that throws selects <b>nothing</b> and does not take the batch down: a broken predicate is a
    /// fail-closed refusal, exactly as an unprimed catalog denies every operation. It is recorded at Debug
    /// rather than Warning because the loud version is per event, which is the noise the whole execution-log
    /// criterion exists to prevent — and because a condition compiled at apply time cannot fail on an author's
    /// mistake, so this is an internal invariant rather than something a descriptor can cause.
    /// </para>
    /// </remarks>
    private static bool Selects(
        CompiledAfterHook hook,
        AlvoEvent @event,
        IPredicateEvaluator evaluator,
        AlvoContext context,
        ILogger logger)
    {
        if (hook.Condition is null)
        {
            return true;
        }

        if (!CarriesWhatItReads(hook, @event, context, logger))
        {
            return false;
        }

        try
        {
            return evaluator.Evaluate(
                hook.Condition, @event.Data.Record ?? AlvoRecord.Empty, @event.Data.OldRecord, context);
        }
        catch (CelFunctionException failure)
        {
            EventLog.ConditionFunctionFailed(
                logger, hook.Path, failure.FunctionName, @event.Id, failure.InnerException ?? failure);
            return false;
        }
        catch (Exception failure)
        {
            EventLog.ConditionRefusedTheHook(logger, hook.Path, @event.Id, failure);
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="event"/> carries every caller value <paramref name="hook"/>'s condition reads; when it
    /// does not, the hook is not selected and the missing reference is recorded at Debug.
    /// </summary>
    private static bool CarriesWhatItReads(CompiledAfterHook hook, AlvoEvent @event, AlvoContext context, ILogger logger)
    {
        if (hook.Required.UserIdMissingFrom(context))
        {
            EventLog.ConditionHasNoActorToRead(logger, hook.Path, @event.Id);
            return false;
        }

        if (UnreadProvenance(hook, @event, context) is { } reference)
        {
            EventLog.ConditionHasNoProvenanceToRead(logger, hook.Path, reference, @event.Id);
            return false;
        }

        return true;
    }

    /// <summary>Reads the entity and the operation out of an event type, or answers <see langword="false"/>.</summary>
    private static bool TryReadSubscription(string type, out string entity, out DataOperation operation)
    {
        entity = string.Empty;
        operation = default;

        var segments = type.Split(TypeSeparator);

        if (segments.Length != TypeSegments
            || !string.Equals(segments[PrefixSegment], DataEventPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        entity = segments[EntitySegment];

        return _operations.TryGetValue(segments[OperationSegment], out operation);
    }

    private const char TypeSeparator = '.';
    private const int TypeSegments = 3;
    private const int PrefixSegment = 0;
    private const int EntitySegment = 1;
    private const int OperationSegment = 2;
    private const string DataEventPrefix = "entity";

    /// <summary>
    /// The third segment of a data event's type, as the emitting driver spells it, mapped onto the operation
    /// whose hook list it selects.
    /// </summary>
    private static readonly Dictionary<string, DataOperation> _operations =
        new(StringComparer.Ordinal)
        {
            ["created"] = DataOperation.Create,
            ["updated"] = DataOperation.Update,
            ["deleted"] = DataOperation.Delete,
        };
}
