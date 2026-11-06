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

    /// <summary>
    /// A command: host C# a caller invokes by name — over HTTP, or in process — after its access rule admits them.
    /// </summary>
    [JsonStringEnumMemberName("command")]
    Command = 1,

    /// <summary>A validate hook: host C# run before a top-level write's transaction opens, network allowed; it can only refuse.</summary>
    [JsonStringEnumMemberName("validate")]
    Validate = 2,

    /// <summary>A before-write hook: host C# run inside a create, update or replace's transaction before the row is written.</summary>
    [JsonStringEnumMemberName("beforeWrite")]
    BeforeWrite = 3,

    /// <summary>An after-write hook: host C# run inside a create, update or replace's transaction after the row and its rollups.</summary>
    [JsonStringEnumMemberName("afterWrite")]
    AfterWrite = 4,

    /// <summary>A before-delete hook: host C# run inside a delete's transaction before the row is removed.</summary>
    [JsonStringEnumMemberName("beforeDelete")]
    BeforeDelete = 5,

    /// <summary>An after-delete hook: host C# run inside a delete's transaction after the row is removed.</summary>
    [JsonStringEnumMemberName("afterDelete")]
    AfterDelete = 6,

    /// <summary>A schedule: host C# run on a cron, once per occurrence across every replica, as a delivery.</summary>
    [JsonStringEnumMemberName("schedule")]
    Schedule = 7,
}
