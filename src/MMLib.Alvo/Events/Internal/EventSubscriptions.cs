using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
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
/// <b>A condition's <c>@user.id</c> is answered from the <em>envelope</em>, not from whoever is dispatching.</b>
/// The caller is built per event out of <see cref="AlvoEvent.AuthId"/>, which is the credential that made the
/// change and the only actor an author can mean — so <c>new.owner_id != @user.id</c> ("don't notify whoever
/// just changed it") compares two real values instead of comparing a row against the framework's own reserved
/// id, which is what a shared <c>AlvoContext.System</c> made it do: never matching in the positive form, and
/// always matching in the negated one. The other two references cannot be answered from an envelope at all and
/// are refused when the hook is compiled (<see cref="AfterHookCompiler"/>), which is what makes building the
/// caller here a complete answer rather than a partial one: <see cref="EnvelopeProvenance"/> holds the rule.
/// </para>
/// <para>
/// <b>An event that records no actor selects no hook that asks who acted.</b> An anonymous write carries no
/// <c>authid</c>, and the reserved all-zero <c>UserId</c> means "no identity" rather than a caller who owns the
/// all-zero rows — so <see cref="CompiledAfterHook.Required"/> gates the hook out instead of letting the
/// comparison run against it. Exactly the <see cref="RequiredContext"/> gate the policy engine applies to a
/// rule, in the same direction: refuse upstream rather than fold an absent operand into a verdict.
/// </para>
/// </remarks>
internal static class EventSubscriptions
{
    /// <summary>The hooks <paramref name="event"/> is subscribed to, in declaration order.</summary>
    /// <param name="catalog">The primed policy catalog the hooks were compiled into.</param>
    /// <param name="event">The event, as the outbox stored it.</param>
    /// <param name="evaluator">The evaluator every condition is judged by.</param>
    /// <param name="logger">Where a condition that threw, or one with no actor to read, is recorded at Debug.</param>
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
    /// The caller one event's conditions resolve <c>@user.id</c> against: the credential the envelope records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoContext.Anonymous"/> when the envelope records nobody, which is what an anonymous write
    /// emits — and <see cref="CompiledAfterHook.Required"/> is what keeps a condition reading <c>@user.id</c>
    /// from being decided against the reserved all-zero id it carries.
    /// </para>
    /// <para>
    /// <b>No tenant and only <see cref="Role.Anon"/>, and neither is observable.</b> <c>@tenant.id</c> and
    /// <c>@user.roles</c> are refused when the hook is compiled, so nothing a condition can name reads either —
    /// which is why this can be honest about carrying neither instead of borrowing the dispatcher's own
    /// <see cref="AlvoContext.System"/> identity, whose <see cref="Role.Admin"/> made
    /// <c>'admin' in @user.roles</c> true for every event.
    /// </para>
    /// </remarks>
    private static AlvoContext CallerOf(AlvoEvent @event) =>
        UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var actor)
            ? new AlvoContext { User = actor, Roles = _noRoles }
            : AlvoContext.Anonymous;

    private static readonly IReadOnlySet<Role> _noRoles = new HashSet<Role> { Role.Anon };

    /// <summary>
    /// Whether <paramref name="hook"/>'s condition holds for <paramref name="event"/>. A hook declaring none
    /// always holds.
    /// </summary>
    /// <remarks>
    /// A condition that throws selects <b>nothing</b> and does not take the batch down: a broken predicate is a
    /// fail-closed refusal, exactly as an unprimed catalog denies every operation. It is recorded at Debug
    /// rather than Warning because the loud version is per event, which is the noise the whole execution-log
    /// criterion exists to prevent — and because a condition compiled at apply time cannot fail on an author's
    /// mistake, so this is an internal invariant rather than something a descriptor can cause.
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

        if (hook.Required.IsMissingFrom(context))
        {
            EventLog.ConditionHasNoActorToRead(logger, hook.Path, @event.Id);
            return false;
        }

        try
        {
            return evaluator.Evaluate(
                hook.Condition, @event.Data.Record ?? AlvoRecord.Empty, @event.Data.OldRecord, context);
        }
        catch (Exception failure)
        {
            EventLog.ConditionRefusedTheHook(logger, hook.Path, @event.Id, failure);
            return false;
        }
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
