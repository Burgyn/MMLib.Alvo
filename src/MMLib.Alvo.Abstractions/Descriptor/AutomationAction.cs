using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// A post-commit action (schema <c>$defs/action</c>), discriminated by its
/// <c>type</c> tag. Durable, retried; payload transformations use JSONata.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebhookAction), "webhook")]
[JsonDerivedType(typeof(EmailAction), "email")]
[JsonDerivedType(typeof(FunctionAction), "function")]
[JsonDerivedType(typeof(EntityUpdateAction), "entity.update")]
[JsonDerivedType(typeof(HttpCallAction), "http.call")]
public abstract record AutomationAction;

/// <summary>Dispatches to a managed webhook endpoint (<c>type: webhook</c>).</summary>
public sealed record WebhookAction : AutomationAction
{
    /// <summary>Name of the endpoint declared under <c>webhooks.endpoints</c>.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Optional JSONata transformation of the outbound payload.</summary>
    public string? Payload { get; init; }
}

/// <summary>Sends an email from a reusable template (<c>type: email</c>).</summary>
public sealed record EmailAction : AutomationAction
{
    /// <summary>Name of the template declared under <c>templates</c>.</summary>
    public required string Template { get; init; }

    /// <summary>Recipient address or a <c>{{...}}</c> template.</summary>
    public required string To { get; init; }

    /// <summary>Optional JSONata transformation producing the template data.</summary>
    public string? Data { get; init; }
}

/// <summary>Invokes a custom function (<c>type: function</c>).</summary>
public sealed record FunctionAction : AutomationAction
{
    /// <summary>Name of the function declared under <c>functions</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Optional JSONata transformation producing the function input.</summary>
    public string? Input { get; init; }
}

/// <summary>Creates or updates a record on an entity (<c>type: entity.update</c>).</summary>
public sealed record EntityUpdateAction : AutomationAction
{
    /// <summary>Target entity name.</summary>
    public required string Entity { get; init; }

    /// <summary>Id or a <c>{{...}}</c> template of the record to update; omit to create a new record.</summary>
    public string? RecordId { get; init; }

    /// <summary>Field patch: field to literal value or tagged CEL expression.</summary>
    public required IReadOnlyDictionary<string, ValueOrExpr> Payload { get; init; }
}

/// <summary>Calls an absolute HTTP URL (<c>type: http.call</c>).</summary>
public sealed record HttpCallAction : AutomationAction
{
    /// <summary>Absolute URL to call.</summary>
    public required string Url { get; init; }

    /// <summary>HTTP method; defaults to POST when omitted.</summary>
    public HttpVerb? Method { get; init; }

    /// <summary>Secret name in the secret store holding request headers; never the value itself.</summary>
    public string? HeadersSecretRef { get; init; }

    /// <summary>Optional JSONata transformation of the outbound payload.</summary>
    public string? Payload { get; init; }
}
