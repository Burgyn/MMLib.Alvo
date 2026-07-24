using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// The closed set of field data types a descriptor may declare (schema <c>$defs/fieldType</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    /// <summary>A short, single-line string (<c>string</c>).</summary>
    [JsonStringEnumMemberName("string")]
#pragma warning disable CA1720
    String,
#pragma warning restore CA1720

    /// <summary>A long, multi-line string (<c>text</c>).</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>A whole number (<c>integer</c>).</summary>
    [JsonStringEnumMemberName("integer")]
#pragma warning disable CA1720
    Integer,
#pragma warning restore CA1720

    /// <summary>A fixed-point decimal, requiring precision and scale (<c>decimal</c>).</summary>
    [JsonStringEnumMemberName("decimal")]
#pragma warning disable CA1720
    Decimal,
#pragma warning restore CA1720

    /// <summary>A boolean (<c>boolean</c>).</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>A calendar date without a time component (<c>date</c>).</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>A date and time (<c>datetime</c>).</summary>
    [JsonStringEnumMemberName("datetime")]
    DateTime,

    /// <summary>A UUID (<c>uuid</c>).</summary>
    [JsonStringEnumMemberName("uuid")]
    Uuid,

    /// <summary>An arbitrary JSON document (<c>json</c>).</summary>
    [JsonStringEnumMemberName("json")]
    Json,

    /// <summary>A closed enumeration of string values, requiring <c>values</c> (<c>enum</c>).</summary>
    [JsonStringEnumMemberName("enum")]
    Enum,

    /// <summary>A foreign-key reference to another entity, requiring <c>entity</c> (<c>ref</c>).</summary>
    [JsonStringEnumMemberName("ref")]
    Ref,
}

/// <summary>Referential-integrity behaviour when a referenced record is deleted (schema <c>field.onDelete</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OnDeleteAction>))]
public enum OnDeleteAction
{
    /// <summary>Block the delete while dependent records exist (<c>restrict</c>).</summary>
    [JsonStringEnumMemberName("restrict")]
    Restrict,

    /// <summary>Delete dependent records along with the target (<c>cascade</c>).</summary>
    [JsonStringEnumMemberName("cascade")]
    Cascade,

    /// <summary>Null out the reference on dependent records (<c>setNull</c>).</summary>
    [JsonStringEnumMemberName("setNull")]
    SetNull,
}

/// <summary>Physical layout of an entity (schema <c>entity.storage</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageMode>))]
public enum StorageMode
{
    /// <summary>A real, introspected/migrated table (<c>physical</c>).</summary>
    [JsonStringEnumMemberName("physical")]
    Physical,

    /// <summary>The shared metadata-driven store — no DDL, instant apply (<c>dynamic</c>).</summary>
    [JsonStringEnumMemberName("dynamic")]
    Dynamic,
}

/// <summary>Multi-tenancy scope of an entity or of runtime-created entities (schema <c>tenancy</c> enum).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityTenancy>))]
public enum EntityTenancy
{
    /// <summary>Rows carry <c>tenant_id</c> and are isolated per tenant (<c>scoped</c>).</summary>
    [JsonStringEnumMemberName("scoped")]
    Scoped,

    /// <summary>Shared reference data visible to all tenants (<c>global</c>).</summary>
    [JsonStringEnumMemberName("global")]
    Global,
}

/// <summary>Aggregate operation of a rollup field (schema <c>field.rollup.op</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RollupOp>))]
public enum RollupOp
{
    /// <summary>Sum of the aggregated child field (<c>sum</c>).</summary>
    [JsonStringEnumMemberName("sum")]
    Sum,

    /// <summary>Count of matching child records (<c>count</c>).</summary>
    [JsonStringEnumMemberName("count")]
    Count,

    /// <summary>Arithmetic mean of the aggregated child field (<c>avg</c>).</summary>
    [JsonStringEnumMemberName("avg")]
    Avg,

    /// <summary>Minimum of the aggregated child field (<c>min</c>).</summary>
    [JsonStringEnumMemberName("min")]
    Min,

    /// <summary>Maximum of the aggregated child field (<c>max</c>).</summary>
    [JsonStringEnumMemberName("max")]
    Max,
}

/// <summary>An enabled authentication identity provider (schema <c>auth.providers</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthProvider>))]
public enum AuthProvider
{
    /// <summary>Credentials managed by Alvo via ASP.NET Core Identity (<c>local</c>).</summary>
    [JsonStringEnumMemberName("local")]
    Local,

    /// <summary>Google sign-in (<c>google</c>).</summary>
    [JsonStringEnumMemberName("google")]
    Google,

    /// <summary>Microsoft sign-in (<c>microsoft</c>).</summary>
    [JsonStringEnumMemberName("microsoft")]
    Microsoft,

    /// <summary>GitHub sign-in (<c>github</c>).</summary>
    [JsonStringEnumMemberName("github")]
    Github,

    /// <summary>Apple sign-in (<c>apple</c>).</summary>
    [JsonStringEnumMemberName("apple")]
    Apple,

    /// <summary>A generic OIDC relying party for a custom identity provider (<c>oidc</c>).</summary>
    [JsonStringEnumMemberName("oidc")]
    Oidc,
}

/// <summary>How an automation rule dispatches over affected rows (schema <c>automationRule.delivery</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryMode>))]
public enum DeliveryMode
{
    /// <summary>One execution per affected row (<c>perItem</c>).</summary>
    [JsonStringEnumMemberName("perItem")]
    PerItem,

    /// <summary>One execution with an array payload, coalescing bulk operations (<c>batch</c>).</summary>
    [JsonStringEnumMemberName("batch")]
    Batch,
}

/// <summary>Execution model of a custom function (schema <c>functions.*.execution</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FunctionExecution>))]
public enum FunctionExecution
{
    /// <summary>Runs in the request path with a short timeout (<c>sync</c>).</summary>
    [JsonStringEnumMemberName("sync")]
    Sync,

    /// <summary>Runs via the outbox and a worker (<c>queued</c>).</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,
}

/// <summary>An HTTP method for a function HTTP trigger or an <c>http.call</c> action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HttpVerb>))]
public enum HttpVerb
{
    /// <summary>HTTP GET.</summary>
    [JsonStringEnumMemberName("GET")]
    Get,

    /// <summary>HTTP POST.</summary>
    [JsonStringEnumMemberName("POST")]
    Post,

    /// <summary>HTTP PUT.</summary>
    [JsonStringEnumMemberName("PUT")]
    Put,

    /// <summary>HTTP PATCH.</summary>
    [JsonStringEnumMemberName("PATCH")]
    Patch,

    /// <summary>HTTP DELETE.</summary>
    [JsonStringEnumMemberName("DELETE")]
    Delete,
}
