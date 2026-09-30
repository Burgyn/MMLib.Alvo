using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>What a trace's header names: the instructions, the provider, the model and the project.</summary>
/// <param name="Instructions">The instructions' version, e.g. <c>alvo-schema-assistant v5</c>.</param>
/// <param name="Provider">The connection's kind.</param>
/// <param name="Model">The model the turn asked for.</param>
/// <param name="Project">The project the turn was about.</param>
internal sealed record TurnHeader(string Instructions, string Provider, string Model, string Project);

/// <summary>How a turn ended, as its trace and its 6203 log line say.</summary>
/// <remarks>
/// <see cref="Abandoned"/> goes beyond D44's three: a caller that stops reading (a closed drawer, a cancelled request)
/// ends the turn too, and the 6203 line is written from a <c>finally</c> so that turn is logged (pre-flight L12). It is
/// never in a trace, because nobody is reading one.
/// </remarks>
internal static class TurnEnd
{
    internal const string Answered = "answered";
    internal const string IterationCap = "iteration-cap";
    internal const string EndpointFailed = "endpoint-failed";
    internal const string Abandoned = "abandoned";
}

/// <summary>
/// A turn's trace (D44): a header, one entry per tool call, and how the turn ended — calls, never prose; scrubbed; and
/// capped at <see cref="MaximumBytes"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured as it is emitted.</b> <see cref="Json"/> is the one serialiser, used both to measure against the cap and
/// to emit, with relaxed escaping so <c>č</c> stays one character, not six. Each entry is scrubbed before it is
/// measured, since a scrub may lengthen a string; the header's final fields are present when an entry is measured,
/// and <see cref="Reserve"/> keeps room for the one written afterwards, <c>droppedCalls</c> (pre-flight H2).
/// </para>
/// <para>
/// <b>Over the cap</b>, an entry first loses its <c>operations</c> (<c>{"omittedBytes": n}</c>) and the header says
/// <c>truncated</c>; if it still crosses, recording stops and <c>droppedCalls</c> says how many calls were left out.
/// The re-measure is quadratic, bounded by the iteration cap's calls and by the cap itself.
/// </para>
/// </remarks>
internal static class TurnTrace
{
    internal const string Format = "alvo.assistant.turn/1";
    internal const int MaximumBytes = 65_536;

    /// <summary>Room kept for the members written after the last measure.</summary>
    private const int Reserve = 64;

    private const string Operations = "operations";
    private const string Arguments = "arguments";
    private const string ResultMember = "result";
    private const string Violations = "violations";

    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };
    private static readonly HashSet<string> _skillReads = new(StringComparer.Ordinal) { "load_skill", "read_skill_resource" };
    private static readonly HashSet<string> _kept = new(StringComparer.Ordinal) { "baseRevision", "skillName", "resourceName" };
    private static readonly string[] _dryRunMembers = ["valid", "revision", "changedPaths", "attemptsLeft", "unchecked", Violations];
    private static readonly string[] _loggedViolationMembers = ["source", "pointer", "code", "severity"];
    private static readonly string[] _loggedOperationMembers = ["op", "path", "from"];

    private static readonly JsonSerializerOptions _writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The trace of a turn's calls, and how it ended.</summary>
    internal static JsonObject Of(TurnHeader header, IReadOnlyList<TracedCall> calls, string end)
    {
        var trace = (JsonObject)SecretScrub.Scrub(Header(header, calls, end))!;
        var entries = trace["calls"]!.AsArray();
        for (var index = 0; index < calls.Count; index++)
        {
            if (!TryAdd(trace, entries, (JsonObject)SecretScrub.Scrub(Entry(calls[index]))!))
            {
                trace["droppedCalls"] = calls.Count - index;
                break;
            }
        }

        return trace;
    }

    /// <summary>The trace as it is emitted — and measured.</summary>
    internal static string Json(JsonNode trace) => trace.ToJsonString(_writer);

    /// <summary>The entry of one call, scrubbed: what a trace holds for it.</summary>
    internal static JsonObject Entry(TracedCall call) => new()
    {
        ["round"] = call.Round,
        ["tool"] = call.Tool,
        [Arguments] = ArgumentsOf(call.Arguments),
        [ResultMember] = ResultOf(call.Tool, call.Result),
    };

    /// <summary>
    /// What of an entry reaches a log line (D44, pre-flight H3): each operation as its <c>op</c>, <c>path</c> and
    /// <c>from</c>, each violation as its <c>source</c>, <c>pointer</c>, <c>code</c> and <c>severity</c>, and a tool
    /// error as its code — never a value, a message or a fix, which may quote the operator's own descriptor.
    /// </summary>
    internal static JsonObject LogLine(JsonObject entry)
    {
        var line = (JsonObject)entry.DeepClone();
        if (line[Arguments] is JsonObject { } arguments && arguments[Operations] is JsonArray operations)
        {
            arguments[Operations] = Reduced(operations, _loggedOperationMembers);
        }

        if (line[ResultMember] is JsonObject { } result)
        {
            result.Remove("message");
            if (result[Violations] is JsonArray violations)
            {
                result[Violations] = Reduced(violations, _loggedViolationMembers);
            }
        }

        return line;
    }

    private static JsonObject Header(TurnHeader header, IReadOnlyList<TracedCall> calls, string end) => new()
    {
        ["format"] = Format,
        ["instructions"] = header.Instructions,
        ["provider"] = header.Provider,
        ["model"] = header.Model,
        ["project"] = header.Project,
        ["baseRevision"] = BaseRevision(calls),
        ["end"] = end,
        ["rounds"] = calls.Count == 0 ? 0 : calls.Max(call => call.Round),
        ["callCount"] = calls.Count,
        ["calls"] = new JsonArray(),
    };

    /// <summary>Adds the entry within the cap: whole, else without its operations, else not at all.</summary>
    private static bool TryAdd(JsonObject trace, JsonArray entries, JsonObject entry)
    {
        entries.Add(entry);
        if (Fits(trace))
        {
            return true;
        }

        trace["truncated"] = true;
        if (entry[Arguments] is JsonObject arguments && arguments[Operations] is { } operations)
        {
            arguments[Operations] = new JsonObject { ["omittedBytes"] = Encoding.UTF8.GetByteCount(Json(operations)) };
            if (Fits(trace))
            {
                return true;
            }
        }

        entries.RemoveAt(entries.Count - 1);
        return false;
    }

    private static bool Fits(JsonObject trace) => Encoding.UTF8.GetByteCount(Json(trace)) + Reserve <= MaximumBytes;

    /// <summary>The revision the first <c>get_descriptor</c> answered, or null.</summary>
    private static int? BaseRevision(IReadOnlyList<TracedCall> calls) =>
        calls.FirstOrDefault(call => call.Tool == "get_descriptor" && call.Result is not null) is { } first
            && Parsed(first.Result!) is JsonObject answer && answer["revision"] is JsonValue revision
            && revision.TryGetValue<int>(out var value)
            ? value
            : null;

    /// <summary>
    /// The arguments a trace keeps: the base revision, the patch (parsed when it arrived as a string), the skill and
    /// resource names, the summary as its length, and any other key by name only.
    /// </summary>
    private static JsonObject ArgumentsOf(IDictionary<string, object?>? arguments)
    {
        var kept = new JsonObject();
        foreach (var (key, value) in arguments ?? new Dictionary<string, object?>())
        {
            switch (key)
            {
                case Operations:
                    kept[key] = Unwrapped(NodeOf(value));
                    break;
                case "summary":
                    kept["summaryChars"] = (NodeOf(value) as JsonValue)?.TryGetValue<string>(out var summary) == true ? summary.Length : 0;
                    break;
                default:
                    kept[key] = _kept.Contains(key) ? NodeOf(value) : null;
                    break;
            }
        }

        return kept;
    }

    private static JsonObject? ResultOf(string tool, string? result) => result switch
    {
        null => null,
        _ when _dryRuns.Contains(tool) => DryRunResult(result),
        _ when tool == "get_descriptor" => new JsonObject { ["revision"] = (Parsed(result) as JsonObject)?["revision"]?.DeepClone() },
        _ when _skillReads.Contains(tool) => new JsonObject { ["chars"] = result.Length, ["found"] = !result.StartsWith("Error:", StringComparison.Ordinal) },
        _ => new JsonObject { ["chars"] = result.Length },
    };

    /// <summary>A dry run's answer: the outcome's members, or the tool error's code and message.</summary>
    private static JsonObject DryRunResult(string result)
    {
        if (Parsed(result) is not JsonObject answer)
        {
            return new JsonObject { ["chars"] = result.Length };
        }

        if (answer.ContainsKey("error"))
        {
            return new JsonObject { ["error"] = answer["error"]?.DeepClone(), ["message"] = answer["message"]?.DeepClone() };
        }

        var kept = new JsonObject();
        foreach (var member in _dryRunMembers.Where(answer.ContainsKey))
        {
            kept[member] = answer[member]?.DeepClone();
        }

        return kept;
    }

    private static JsonArray Reduced(JsonArray items, string[] members) =>
    [
        .. items.Select(item => item is JsonObject whole
            ? new JsonObject(members.Where(whole.ContainsKey).Select(member => KeyValuePair.Create(member, whole[member]?.DeepClone())))
            : null),
    ];

    private static JsonNode? Unwrapped(JsonNode? operations) =>
        operations is JsonValue value && value.TryGetValue<string>(out var text) && Parsed(text) is JsonArray parsed ? parsed : operations;

    /// <summary>An argument as JSON: the model's arguments arrive as <see cref="JsonElement"/>s, a script's as CLR values.</summary>
    private static JsonNode? NodeOf(object? value) => value switch
    {
        null => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        bool flag => JsonValue.Create(flag),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonNode? Parsed(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
