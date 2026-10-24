using System.Text.Json.Serialization;

namespace MMLib.Alvo.Management;

/// <summary>What kind of host code an extension is.</summary>
/// <remarks>
/// Append only: a later version adds the kinds it learns to register after the existing members and never reorders or
/// renumbers them. A client should read an unknown value as opaque; a typed .NET client older than the server cannot —
/// <see cref="JsonStringEnumConverter{TEnum}"/> throws on a member it does not know — so such a client reads the raw
/// string when it must tolerate a newer server.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlvoExtensionKind>))]
public enum AlvoExtensionKind
{
    /// <summary>An event handler: host C# run after a write commits, once per matching event, at least once.</summary>
    [JsonStringEnumMemberName("eventHandler")]
    EventHandler = 0,
}
