using System.Text.Json.Serialization;

namespace MMLib.Alvo.Api;

/// <summary>
/// One machine-readable reason a request was refused. A refusal carries a <em>list</em> of these rather
/// than a single sentence, because §0 principle 4's primary reader is an agent that has to decide what to
/// change: a 422 saying only "the query is malformed" costs it a request per guess.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here may echo caller-supplied text.</b> A refusal is answered before authorization on the
/// query path, so it is the cheapest oracle in the framework: a message naming the offending field answers
/// "does this entity have a field called X" one request at a time, and a message quoting the offending
/// value puts attacker-controlled bytes into every log that records the response. Every producer therefore
/// builds <see cref="Message"/> and <see cref="FixSuggestion"/> from constants plus server-owned values
/// (an option's configured bound, the port's own limits, the operator allow-list) — never from the
/// request. <see cref="Pointer"/> names the parameter's <em>role</em> for the same reason: in
/// PostgREST's grammar a filter's parameter name <em>is</em> the field name.
/// </para>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than left to the
/// host's naming policy, for the reason <c>Internal.DataApiJson</c> gives: this is a shape Alvo publishes,
/// and an embedded host configuring camelCase for its own endpoints must not rename it.
/// </para>
/// </remarks>
/// <param name="Pointer">
/// A JSON Pointer (RFC 6901) into the request body, or the role of the query-string parameter the refusal
/// concerns (<c>filter</c>, <c>order</c>, <c>limit</c>, <c>offset</c>, <c>after</c>, <c>select</c>).
/// </param>
/// <param name="Code">A stable kebab-case code, e.g. <c>required</c>, <c>max-length</c>, <c>unavailable-field</c>.</param>
/// <param name="Message">A human sentence, free of caller-supplied text.</param>
/// <param name="FixSuggestion">What to change — §0 principle 4 makes this part of the contract, not a nicety.</param>
public sealed record AlvoViolation(
#pragma warning disable CA1720 // "Pointer" is RFC 6901's own term for this member, not the CLR's.
    [property: JsonPropertyName("pointer")] string Pointer,
#pragma warning restore CA1720
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("fixSuggestion")] string? FixSuggestion);
