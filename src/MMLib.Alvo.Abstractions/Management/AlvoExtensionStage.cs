using System.Text.Json.Serialization;

namespace MMLib.Alvo.Management;

/// <summary>When, relative to a write, an extension runs.</summary>
/// <remarks>
/// Append only, never reordered or renumbered — the same rule, and the same caveat for an older typed client, as
/// <see cref="AlvoExtensionKind"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlvoExtensionStage>))]
public enum AlvoExtensionStage
{
    /// <summary>After the write's transaction has committed, from the outbox: it can never undo the write.</summary>
    [JsonStringEnumMemberName("postCommit")]
    PostCommit = 0,

    /// <summary>On an HTTP request, outside any transaction: the extension opens its own, if it writes.</summary>
    [JsonStringEnumMemberName("http")]
    Http = 1,

    /// <summary>
    /// Before any transaction is open, in the flow of whatever invoked it: a network call is allowed, and its writes open
    /// their own unit.
    /// </summary>
    [JsonStringEnumMemberName("preTransaction")]
    PreTransaction = 2,

    /// <summary>Inside the write's own transaction: it can refuse or undo the write, and may not leave the process.</summary>
    [JsonStringEnumMemberName("inTransaction")]
    InTransaction = 3,
}
