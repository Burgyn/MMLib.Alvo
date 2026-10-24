using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>How a stored envelope is read for a subscription: its data-event type, its caller as at the event, and a condition over its images.</summary>
/// <remarks>
/// Shared by <see cref="EventSubscriptions"/> (descriptor after-hooks) and <see cref="HandlerCondition"/> (host handlers),
/// so the two subscription kinds cannot drift. The caller is built from the envelope — <c>authid</c>, <c>tenantid</c>,
/// <c>authclaims</c> — never from whoever dispatches; what the envelope does not carry is left absent.
/// </remarks>
internal static class EnvelopeReading
{
    private const char TypeSeparator = '.';
    private const int TypeSegments = 3;
    private const int PrefixSegment = 0;
    private const int EntitySegment = 1;
    private const int OperationSegment = 2;
    private const string DataEventPrefix = "entity";

    private static readonly IReadOnlySet<Role> _noRoles = new HashSet<Role> { Role.Anon };

    private static readonly Dictionary<string, DataOperation> _operations = new(StringComparer.Ordinal)
    {
        ["created"] = DataOperation.Create,
        ["updated"] = DataOperation.Update,
        ["deleted"] = DataOperation.Delete,
    };

    /// <summary>Reads the entity and the operation out of a data event's type; anything else answers <see langword="false"/>.</summary>
    internal static bool TryReadDataType(string type, out string entity, out DataOperation operation)
    {
        entity = string.Empty;
        operation = default;
        var segments = type.Split(TypeSeparator);
        if (segments.Length != TypeSegments || !string.Equals(segments[PrefixSegment], DataEventPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        entity = segments[EntitySegment];

        return _operations.TryGetValue(segments[OperationSegment], out operation);
    }

    /// <summary>The caller one event's conditions resolve against: the credential, tenant and roles the envelope records.</summary>
    /// <remarks>
    /// What the envelope does not record is left absent — the all-zero id, no tenant, only <see cref="Role.Anon"/> —
    /// and never borrowed from the dispatcher's own <see cref="AlvoContext.System"/> identity. Each subscriber's gate
    /// keeps a condition reading an absent value from being decided against it. <c>authclaims</c> is parsed once here,
    /// and whether it named any role travels with the context.
    /// </remarks>
    internal static EnvelopeCaller CallerOf(AlvoEvent @event)
    {
        var actor = UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var user) ? user : default;
        TenantId? tenant = TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var names = AlvoEventProvenance.RolesIn(@event.AuthClaims);
        var roles = names is { Count: > 0 } ? RoleCatalog.Create(names).Resolve(names) : null;

        return new EnvelopeCaller(new AlvoContext { User = actor, Roles = roles ?? _noRoles, Tenant = tenant }, roles is not null);
    }

    /// <summary>
    /// Evaluates <paramref name="condition"/> over the event's images, answering <c>changed(f)</c> from the envelope's
    /// own change list whenever the evaluator can (spec E7); a host's own evaluator compares the images.
    /// </summary>
    /// <remarks>
    /// The list is the envelope's one account of what moved: it is computed from the full images before
    /// <c>events.exclude</c> strips them, so it still names an excluded field the stripped images would read as
    /// <see langword="null"/> on both sides — which would make <c>changed(f)</c> never hold and its negation hold on
    /// every update.
    /// </remarks>
    internal static bool Holds(CompiledExpression condition, AlvoEventData data, IPredicateEvaluator evaluator, AlvoContext context) =>
        evaluator is IChangeListPredicateEvaluator changeAware
            ? changeAware.Evaluate(condition, data.Record ?? AlvoRecord.Empty, data.OldRecord, data.Changed, context)
            : evaluator.Evaluate(condition, data.Record ?? AlvoRecord.Empty, data.OldRecord, context);
}

/// <summary>One event's caller, and whether its <c>authclaims</c> named any role — parsed once per event.</summary>
/// <param name="Context">The caller conditions are evaluated against.</param>
/// <param name="CarriesRoles">Whether <see cref="AlvoContext.Roles"/> came from the envelope rather than standing in for none.</param>
internal readonly record struct EnvelopeCaller(AlvoContext Context, bool CarriesRoles);
