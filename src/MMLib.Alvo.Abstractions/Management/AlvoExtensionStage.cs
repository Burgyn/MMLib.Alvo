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
}
