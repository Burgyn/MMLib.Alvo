namespace MMLib.Alvo.Descriptor;

/// <summary>
/// A custom logic function backed by a <c>.csx</c> script (schema <c>functions.*</c>).
/// Trust level: admin-level code.
/// </summary>
public sealed record FunctionDescriptor
{
    /// <summary>Relative path to the <c>.csx</c> file inside the descriptor bundle.</summary>
    public required string Script { get; init; }

    /// <summary>Optional trigger; a function without one is only invocable from a <c>function</c> action.</summary>
    public FunctionTrigger? Trigger { get; init; }

    /// <summary>Execution model; defaults to <see cref="FunctionExecution.Queued"/> when omitted.</summary>
    public FunctionExecution? Execution { get; init; }
}

/// <summary>
/// The trigger of a function (schema <c>functions.*.trigger</c>): exactly one of
/// <see cref="Http"/>, <see cref="Schedule"/>, or <see cref="Event"/> is set.
/// </summary>
public sealed record FunctionTrigger
{
    /// <summary>Exposes the function at an HTTP route (also an inbound receiver).</summary>
    public FunctionHttpTrigger? Http { get; init; }

    /// <summary>Cron expression (5 fields, UTC) that runs the function.</summary>
    public string? Schedule { get; init; }

    /// <summary>Event pattern that runs the function.</summary>
    public string? Event { get; init; }
}

/// <summary>An HTTP route trigger for a function (schema <c>functions.*.trigger.http</c>).</summary>
public sealed record FunctionHttpTrigger
{
    /// <summary>Route path, starting with <c>/</c>.</summary>
    public required string Route { get; init; }

    /// <summary>HTTP method; defaults to POST when omitted.</summary>
    public HttpVerb? Method { get; init; }
}
