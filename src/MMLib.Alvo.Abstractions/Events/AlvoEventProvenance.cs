using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Events;

/// <summary>
/// <b>The one authority on the provenance attributes of an event envelope</b>: how the caller authenticated,
/// which credential acted, and which flow the event belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public and shared because there are now two emit paths, in two assemblies.</b> A data event is built by
/// the EF driver, which depends on this package and nothing else; a custom application event is built by the
/// core's <c>IAlvoEvents</c> implementation. When the derivation was private to the driver, the second path
/// had to restate it — and a second copy of "which caller is the system caller" is how a system-made change
/// comes to be reported as an ordinary caller's on one path and not the other. That mattering is not
/// hypothetical here: the whole point of refusing a host the <c>entity.</c> namespace is that an event's
/// provenance is trusted.
/// </para>
/// <para>
/// <b>Authentication, never authorization.</b> A role is not an answer here — a subscriber has to tell "the
/// framework did this" from "the originator did this", and a role says neither. It is also why an envelope
/// carries the role set only as <c>authclaims</c>, a snapshot for after-hook conditions; see
/// <c>docs/architecture/events.md</c>.
/// </para>
/// </remarks>
public static class AlvoEventProvenance
{
    private const string RolesClaim = "roles";

    /// <summary>How <paramref name="context"/> authenticated, as <see cref="AlvoEvent.AuthType"/> spells it.</summary>
    /// <param name="context">The caller the event is emitted for.</param>
    /// <returns>One of the <see cref="AlvoEventAuthType"/> values.</returns>
    public static string AuthTypeOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.ActorKind switch
        {
            AlvoActorKind.Anonymous => AlvoEventAuthType.Unauthenticated,
            AlvoActorKind.System => AlvoEventAuthType.System,
            AlvoActorKind.ApiKey => AlvoEventAuthType.ApiKey,
            AlvoActorKind.User => AlvoEventAuthType.User,
            _ => AlvoEventAuthType.Unknown,
        };
    }

    /// <summary>
    /// Which credential acted, or <see langword="null"/> when none did.
    /// </summary>
    /// <param name="context">The caller the event is emitted for.</param>
    /// <remarks>
    /// The anonymous caller's reserved all-zero id means "no identity", so reporting it would assert that an
    /// identified caller made the change.
    /// </remarks>
    public static string? AuthIdOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User == default ? null : context.User.Value.ToString();
    }

    /// <summary>
    /// The id everything in one end-to-end flow shares: the ambient W3C trace id when there is one, and
    /// otherwise the event's own id.
    /// </summary>
    /// <param name="eventId">The event's own <see cref="AlvoEvent.Id"/>, used when no trace is ambient.</param>
    /// <remarks>
    /// <para>
    /// <see cref="Activity"/> is in the BCL, so this needs no dependency, and the trace id is exactly what the
    /// specification's end-to-end trace asks for. It falls back to the event's own id rather than to
    /// <see langword="null"/> because the attribute is required — an event with no ambient trace still belongs
    /// to a flow, namely its own.
    /// </para>
    /// <para>
    /// Only a W3C-format activity counts: a hierarchical one has no trace id, and its all-zero placeholder would put
    /// every such event in one flow.
    /// </para>
    /// </remarks>
    public static string CorrelationIdOf(Guid eventId) =>
        AmbientW3CActivity?.TraceId.ToString() ?? eventId.ToString();

    /// <summary>The ambient <see cref="Activity"/> when it is in W3C format, the only one that carries a trace context.</summary>
    internal static Activity? AmbientW3CActivity =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;

    /// <summary>
    /// The provenance an operation reports: <paramref name="context"/>'s own, merged with the ambient flow's.
    /// </summary>
    /// <param name="context">The caller.</param>
    /// <returns>
    /// Depth is the larger of the two, so code can raise the depth it reports and never lower it; correlation and
    /// causation are the explicit ones when set and the ambient ones otherwise; the trace context travels as a pair from
    /// whichever side names a <c>traceparent</c>.
    /// </returns>
    public static AlvoProvenance Effective(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stated = context.Provenance;
        var ambient = AlvoFlow.Provenance ?? AlvoProvenance.None;
        var trace = stated.TraceParent is not null ? stated : ambient;

        return new AlvoProvenance
        {
            CorrelationId = stated.CorrelationId ?? ambient.CorrelationId,
            CausationId = stated.CausationId ?? ambient.CausationId,
            Depth = Math.Max(stated.Depth, ambient.Depth),
            TraceParent = trace.TraceParent,
            TraceState = trace.TraceState,
        };
    }

    /// <summary>The caller's roles as the authcontext <c>authclaims</c> JSON string, in ordinal order.</summary>
    internal static string AuthClaimsOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(RolesClaim);
            foreach (var role in context.Roles.Select(role => role.Name).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(role);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The role names an <c>authclaims</c> string carries, or <see langword="null"/> when it carries none readably.</summary>
    /// <remarks>Tolerant on purpose: a dispatcher must treat a foreign or damaged value as absent — fail-closed — never throw.</remarks>
    internal static IReadOnlyList<string>? RolesIn(string? authClaims)
    {
        if (string.IsNullOrWhiteSpace(authClaims))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(authClaims);
            return RoleNames(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static List<string>? RoleNames(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(RolesClaim, out var roles)
        && roles.ValueKind == JsonValueKind.Array
        && roles.EnumerateArray().All(role => role.ValueKind == JsonValueKind.String)
            ? [.. roles.EnumerateArray().Select(role => role.GetString()!)]
            : null;
}
