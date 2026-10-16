using System.Text.RegularExpressions;

namespace MMLib.Alvo.Events;

/// <summary>
/// The W3C Trace Context (Level 1) grammar an envelope's <c>traceparent</c> and <c>tracestate</c> are held to before
/// they are stamped — the CloudEvents Distributed Tracing extension carries exactly those two header values.
/// </summary>
/// <remarks>
/// <para>
/// Both values may come from outside: an ambient activity's <c>tracestate</c> is the inbound request header, and a
/// host may state either on <see cref="AlvoProvenance"/>. A value outside the grammar is dropped rather than thrown on,
/// because a trace context is diagnostic and a write must never fail on one.
/// </para>
/// <para>
/// <b>Deliberate deviation:</b> only version <c>00</c> — the one version the specification defines — is accepted. W3C
/// lets a receiver parse a higher version's first four fields and ignore the rest; an envelope would then carry that
/// unvalidated rest verbatim, so a higher version restarts the trace instead, which W3C also permits.
/// </para>
/// </remarks>
internal static partial class W3CTraceContext
{
    /// <summary>
    /// The longest <c>tracestate</c> stamped: W3C asks a vendor to propagate at least 512 characters, and a value past
    /// it is dropped whole rather than truncated, since a truncated list can name a different state.
    /// </summary>
    internal const int MaxTraceStateLength = 512;

    /// <summary>The most list members a <c>tracestate</c> may hold (W3C).</summary>
    internal const int MaxTraceStateMembers = 32;

    /// <summary>
    /// Whether <paramref name="traceParent"/> is a version-<c>00</c> <c>traceparent</c> naming a real trace and parent:
    /// lowercase hex, and neither id all zeros.
    /// </summary>
    internal static bool IsTraceParent(string traceParent) =>
        TraceParentRegex().Match(traceParent) is { Success: true } match
        && match.Groups[TraceIdGroup].Value != InvalidTraceId
        && match.Groups[ParentIdGroup].Value != InvalidParentId;

    /// <summary>The trace-id segment of a valid <paramref name="traceParent"/>, or <see langword="null"/>.</summary>
    internal static string? TraceIdOf(string? traceParent) =>
        traceParent is not null && IsTraceParent(traceParent)
            ? TraceParentRegex().Match(traceParent).Groups[TraceIdGroup].Value
            : null;

    /// <summary>
    /// Whether <paramref name="traceState"/> is a W3C <c>tracestate</c> list: at most
    /// <see cref="MaxTraceStateMembers"/> members, each empty or <c>key=value</c>, no key twice, and at most
    /// <see cref="MaxTraceStateLength"/> characters in all.
    /// </summary>
    internal static bool IsTraceState(string traceState)
    {
        if (traceState.Length > MaxTraceStateLength)
        {
            return false;
        }

        var members = traceState.Split(',');
        if (members.Length > MaxTraceStateMembers)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);

        return members.All(member => IsMember(member, keys));
    }

    private static bool IsMember(string member, HashSet<string> keys) =>
        TraceStateMemberRegex().Match(member) is { Success: true } match
        && (!match.Groups[KeyGroup].Success || keys.Add(match.Groups[KeyGroup].Value));

    private const string TraceIdGroup = "traceid";
    private const string ParentIdGroup = "parentid";
    private const string KeyGroup = "key";
    private const string InvalidTraceId = "00000000000000000000000000000000";
    private const string InvalidParentId = "0000000000000000";

    [GeneratedRegex(
        @"\A00-(?<traceid>[0-9a-f]{32})-(?<parentid>[0-9a-f]{16})-[0-9a-f]{2}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex TraceParentRegex();

    [GeneratedRegex(
        @"\A[ \t]*(?:(?<key>[a-z][a-z0-9_\-*/]{0,255}|[a-z0-9][a-z0-9_\-*/]{0,240}@[a-z][a-z0-9_\-*/]{0,13})"
        + @"=[\x20-\x2b\x2d-\x3c\x3e-\x7e]{0,255}[\x21-\x2b\x2d-\x3c\x3e-\x7e])?[ \t]*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex TraceStateMemberRegex();
}
