using System.Diagnostics;

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
/// carries no role list at all; see <c>docs/architecture/events.md</c>.
/// </para>
/// </remarks>
public static class AlvoEventProvenance
{
    /// <summary>How <paramref name="context"/> authenticated, as <see cref="AlvoEvent.AuthType"/> spells it.</summary>
    /// <param name="context">The caller the event is emitted for.</param>
    /// <returns>One of the <see cref="AlvoEventAuthType"/> values.</returns>
    public static string AuthTypeOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User == _anonymousUser ? AlvoEventAuthType.Anonymous
            : context.User == _systemUser ? AlvoEventAuthType.System
            : AlvoEventAuthType.ApiKey;
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
        return context.User == _anonymousUser ? null : context.User.Value.ToString();
    }

    /// <summary>
    /// The id everything in one end-to-end flow shares: the ambient W3C trace id when there is one, and
    /// otherwise the event's own id.
    /// </summary>
    /// <param name="eventId">The event's own <see cref="AlvoEvent.Id"/>, used when no trace is ambient.</param>
    /// <remarks>
    /// <see cref="Activity"/> is in the BCL, so this needs no dependency, and the trace id is exactly what the
    /// specification's end-to-end trace asks for. It falls back to the event's own id rather than to
    /// <see langword="null"/> because the attribute is required — an event with no ambient trace still belongs
    /// to a flow, namely its own.
    /// </remarks>
    public static string CorrelationIdOf(Guid eventId) =>
        Activity.Current?.TraceId.ToString() ?? eventId.ToString();

    /// <inheritdoc cref="AuthTypeOf"/>
    private static readonly UserId _anonymousUser = AlvoContext.Anonymous.User;

    /// <inheritdoc cref="AuthTypeOf"/>
    /// <remarks>
    /// Read off <see cref="AlvoContext.System"/> rather than restated, so the reserved id has one authority:
    /// a second copy of that <see cref="Guid"/> would let the port move it and leave every system-made change
    /// reported as an ordinary caller's.
    /// </remarks>
    private static readonly UserId _systemUser = AlvoContext.System(tenant: null).User;
}
