namespace MMLib.Alvo.Events;

/// <summary>
/// The one authority for how an <see cref="AlvoEvent"/> is spelled on the wire: every CloudEvents
/// attribute name Alvo writes, and which of them are extensions.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the names have a single source. <see cref="AlvoEventJson"/> writes and reads through these
/// members rather than through literals, the conformance oracle iterates
/// <see cref="Extensions"/> to prove every one of them satisfies CloudEvents' naming rule, and
/// <c>docs/architecture/events.md</c> documents the same list — three readers that would otherwise
/// each carry their own spelling.
/// </para>
/// <para>
/// <b>Why these are <see langword="const"/> where <see cref="Data.AlvoFilter.MaxDepth"/> is deliberately a
/// property.</b> That warning is about a value that could sensibly change: a public
/// <see langword="const"/> is inlined at each consumer's compile time, so a driver compiled against one
/// value and a framework enforcing another disagree silently. These values cannot change — they are frozen
/// by CloudEvents v1.0.2, and a different name would be a different specification, not a new Alvo
/// decision.
/// </para>
/// <para>
/// <b>The naming rule that decides every member here.</b> Attribute names MUST consist of lower-case
/// ASCII letters and digits only, and SHOULD NOT exceed 20 characters (CloudEvents v1.0.2, <em>Attribute
/// Naming Convention</em>). So <c>payload_version</c>, <c>chain-depth</c> and <c>old_record</c> are
/// illegal as attribute names — the first two become <see cref="PayloadVersion"/> and
/// <see cref="ChainDepth"/>, and the row images move inside <see cref="Data"/>, where the JSON is
/// unrestricted and <c>snake_case</c> is fine.
/// </para>
/// </remarks>
public static class AlvoEventAttributes
{
    /// <summary>The CloudEvents specification version the event conforms to; the wire value is <c>1.0</c>.</summary>
    public const string SpecVersion = "specversion";

    /// <summary>The event's unique identifier within its <see cref="Source"/>.</summary>
    public const string Id = "id";

    /// <summary>The context the event occurred in, as a URI reference.</summary>
    public const string Source = "source";

    /// <summary>The event type, <c>entity.{entity}.{operation}</c> for a data change.</summary>
    public const string Type = "type";

    /// <summary>When the change committed, as an RFC 3339 timestamp in UTC.</summary>
    public const string Time = "time";

    /// <summary>The subject of the event within its source, <c>{entity}/{id}</c> for a data change.</summary>
    public const string Subject = "subject";

    /// <summary>The media type of <see cref="Data"/>; always <c>application/json</c> here.</summary>
    public const string DataContentType = "datacontenttype";

    /// <summary>The key events for one row are ordered by; the <b>registered</b> Partitioning extension.</summary>
    public const string PartitionKey = "partitionkey";

    /// <summary>The version of the <see cref="Data"/> shape, so an in-process subscriber can switch on an integer.</summary>
    public const string PayloadVersion = "payloadversion";

    /// <summary>How many events deep the causation chain is; <c>0</c> for a change a caller made directly.</summary>
    public const string ChainDepth = "chaindepth";

    /// <summary>How the caller authenticated; see <see cref="AlvoEventAuthType"/>.</summary>
    public const string AuthType = "authtype";

    /// <summary>Which credential acted, when there is one.</summary>
    public const string AuthId = "authid";

    /// <summary>The id shared by everything in one end-to-end flow.</summary>
    public const string CorrelationId = "correlationid";

    /// <summary>The id of the immediate cause of this event, when it had one.</summary>
    public const string CausationId = "causationid";

    /// <summary>The event payload: the row images and the changed-field list.</summary>
    public const string Data = "data";

    /// <summary>
    /// The standard (specification-defined) attribute names Alvo writes, in wire order.
    /// </summary>
    /// <remarks>
    /// The conformance oracle checks this list against the SDK's own set of well-known v1.0 attributes,
    /// which is what catches a near-miss like <c>datacontentype</c> that no reading of the spec reliably
    /// does. <see cref="Data"/> is absent on purpose: it is the payload, not a context attribute.
    /// </remarks>
    public static IReadOnlyList<string> Standard { get; } =
        [SpecVersion, Id, Source, Type, Time, Subject, DataContentType];

    /// <summary>
    /// The extension attribute names Alvo writes, in wire order.
    /// </summary>
    /// <remarks>
    /// Extensions are serialized as <b>flat top-level</b> JSON members, exactly like standard attributes
    /// (CloudEvents v1.0.2, JSON format) — a nested <c>extensions</c> object is non-conformant. Only
    /// <see cref="PartitionKey"/> is in the v1.0.2 registry; the rest are Alvo's or the community's, and
    /// each one's provenance is recorded on the matching <see cref="AlvoEvent"/> member.
    /// </remarks>
    public static IReadOnlyList<string> Extensions { get; } =
        [PartitionKey, PayloadVersion, ChainDepth, AuthType, AuthId, CorrelationId, CausationId];
}

/// <summary>
/// The three values <see cref="AlvoEvent.AuthType"/> takes: how the caller behind a change authenticated.
/// </summary>
/// <remarks>
/// It answers authentication, never authorization — a role is not a value here. The distinction matters
/// because an after-hook has to be able to tell "the framework did this" from "the originator did this"
/// (spec §3.3), and one opaque actor string cannot carry it.
/// </remarks>
public static class AlvoEventAuthType
{
    /// <summary>The caller presented an API key.</summary>
    public const string ApiKey = "apikey";

    /// <summary>Alvo itself made the change, running as the system rather than as a caller.</summary>
    public const string System = "system";

    /// <summary>The caller presented no credential at all.</summary>
    public const string Anonymous = "anon";
}
