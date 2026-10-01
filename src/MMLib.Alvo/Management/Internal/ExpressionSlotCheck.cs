using MMLib.Alvo.Descriptor;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// What apply would say about <b>one</b> expression slot: the candidate is spliced into the descriptor and the
/// very validator apply runs judges the whole document; only the findings at or under the slot are kept.
/// </summary>
/// <remarks>
/// Compiling the source alone is not apply's check: every slot adds post-compile refusals (role literals, hook
/// phase and envelope, mutate type fit, computed shape and render). Reusing the validator is how this stays
/// the same code path rather than a second opinion that drifts.
/// </remarks>
internal static class ExpressionSlotCheck
{
    private const string MutateSegment = "mutate";
    private const int MutatePathLength = 8;
    private const int MaxNamedPlaces = 3;
    private const string Placeholder = "true";

    /// <summary>The start of the message a candidate-caused schema refusal carries.</summary>
    internal const string SchemaRefusalPrefix = "The schema refuses this value";

    /// <summary>The validator's findings at or under <paramref name="pointer"/> with <paramref name="source"/> in place.</summary>
    /// <param name="validator">The validator apply uses.</param>
    /// <param name="descriptorJson">The working-copy descriptor.</param>
    /// <param name="pointer">The slot's RFC 6901 pointer; the slot must already exist.</param>
    /// <param name="source">The candidate expression, as typed.</param>
    /// <exception cref="ManagementRequestException">The request cannot be answered as sent.</exception>
    internal static IReadOnlyList<DescriptorValidationError> Check(
        IDescriptorValidator validator, string descriptorJson, string pointer, string source)
    {
        var segments = JsonPointerPath.Segments(pointer);
        var findings = Judge(validator, descriptorJson, segments, pointer, source);
        var atSlot = findings.Where(f => JsonPointerPath.IsAtOrUnder(f.Path, pointer)).ToList();
        if (atSlot.Any(IsError) || SchemaPlaces(findings).Count == 0)
        {
            return atSlot;
        }

        var baseline = SchemaPlaces(Judge(validator, descriptorJson, segments, pointer, Placeholder));

        return [.. atSlot, baseline.Count == 0 ? CandidateRefused(findings, pointer) : NotJudged(baseline, pointer)];
    }

    /// <summary>Splices <paramref name="source"/> into the descriptor and runs the validator apply runs.</summary>
    private static IReadOnlyList<DescriptorValidationError> Judge(
        IDescriptorValidator validator, string descriptorJson, IReadOnlyList<string> segments, string pointer, string source)
    {
        var root = Parse(descriptorJson);
        Splice(root, segments, pointer, source);

        return validator.Validate(root.ToJsonString()).Errors;
    }

    private static bool IsError(DescriptorValidationError finding) => finding.Severity == DescriptorValidationSeverity.Error;

    /// <summary>
    /// The one error that says nothing was judged, when the schema fails somewhere <b>else</b>: the validator runs
    /// its rule, computed and owner passes only over a descriptor the schema accepts, so a slot's own errors are not
    /// reported while any schema error stands. A pass here would be a pass for something nobody looked at.
    /// </summary>
    private static DescriptorValidationError NotJudged(IReadOnlyList<string> elsewhere, string pointer) => new(
        pointer,
        $"This expression was not judged: the descriptor fails the schema elsewhere ({string.Join(", ", elsewhere)}). "
        + "Fix those first, then it is checked.",
        "Fix the schema errors named above in the working copy (an apply reports every one of them); "
        + "the expression is checked as soon as the descriptor passes the schema.",
        DescriptorValidationSeverity.Error);

    /// <summary>
    /// A schema refusal the validator reports on a node <b>above</b> the slot (a mutate target is a <c>oneOf</c>, so
    /// its value fails on the action), caused by the candidate: the descriptor is schema-valid with a placeholder.
    /// </summary>
    private static DescriptorValidationError CandidateRefused(IReadOnlyList<DescriptorValidationError> findings, string pointer)
    {
        var refusal = findings.First(f => JsonPointerPath.IsSchemaPath(f.Path));

        return new DescriptorValidationError(
            pointer,
            $"{SchemaRefusalPrefix} (at '{JsonPointerPath.Shorten(JsonPointerPath.Normalise(refusal.Path))}'): {refusal.Message}",
            refusal.FixSuggestion,
            DescriptorValidationSeverity.Error);
    }

    /// <summary>Up to three places the schema fails, deepest first-come: an ancestor of another failing place is only its echo.</summary>
    private static List<string> SchemaPlaces(IReadOnlyList<DescriptorValidationError> findings)
    {
        var places = findings
            .Where(f => JsonPointerPath.IsSchemaPath(f.Path))
            .Select(f => JsonPointerPath.Normalise(f.Path))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return [.. places
            .Where(place => !places.Any(other => other.StartsWith(place + "/", StringComparison.Ordinal)))
            .Take(MaxNamedPlaces)
            .Select(place => $"'{JsonPointerPath.Shorten(place)}'")];
    }

    private static JsonObject Parse(string descriptorJson)
    {
        try
        {
            // Refused up front: a duplicate key would otherwise throw lazily, deep inside the walk.
            var options = new JsonDocumentOptions { AllowDuplicateProperties = false };

            return JsonNode.Parse(descriptorJson, documentOptions: options) as JsonObject ?? throw NotADescriptor();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ? DuplicateProperty(ex) : NotADescriptor();
        }
    }

    private static void Splice(JsonNode root, IReadOnlyList<string> segments, string pointer, string source)
    {
        var parent = Walk(root, segments.Take(segments.Count - 1), pointer);
        var last = segments[^1];
        var value = IsMutateValue(segments) ? new JsonObject { ["$cel"] = source } : (JsonNode)JsonValue.Create(source)!;

        switch (parent)
        {
            case JsonObject obj when obj.ContainsKey(last):
                obj[last] = value;
                break;
            case JsonArray array when JsonPointerPath.TryIndex(last, array.Count, out var index):
                array[index] = value;
                break;
            default:
                throw Absent(pointer);
        }
    }

    private static JsonNode Walk(JsonNode root, IEnumerable<string> segments, string pointer)
    {
        var node = root;
        foreach (var segment in segments)
        {
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var child) && child is not null => child,
                JsonArray array when JsonPointerPath.TryIndex(segment, array.Count, out var i) && array[i] is not null => array[i]!,
                _ => throw Absent(pointer),
            };
        }

        return node;
    }

    /// <summary>
    /// A mutate target holds <c>{"$cel": source}</c>, the one slot that is not a bare string. Anchored on the whole
    /// documented shape, so an entity or field that happens to be named <c>mutate</c> is not misread.
    /// </summary>
    private static bool IsMutateValue(IReadOnlyList<string> segments) =>
        segments.Count == MutatePathLength
        && segments[0] == "entities" && segments[2] == "hooks"
        && segments[5] == "action" && segments[6] == MutateSegment;

    private static ManagementRequestException NotADescriptor() => new(
        "The 'descriptor' is not a JSON object. Send the working-copy descriptor exactly as the dashboard holds it.");

    /// <summary>The parser's own text names the property and its position; the client needs both to find it.</summary>
    private static ManagementRequestException DuplicateProperty(Exception parserError) => new(
        $"The 'descriptor' has a duplicate property ({parserError.Message}). JSON objects must not repeat a key; "
        + "send the working-copy descriptor exactly as the dashboard holds it.");

    private static ManagementRequestException Absent(string pointer) => new(
        $"'{pointer}' does not exist in the descriptor sent. The slot must already be in the descriptor — add the "
        + "rule or hook to the working copy first, then check the expression in it.");
}
