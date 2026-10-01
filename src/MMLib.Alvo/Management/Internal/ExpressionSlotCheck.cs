using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
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
    private const int MaxArrayLength = 2_000;
    private const string Placeholder = "true";

    /// <summary>
    /// The start of the message that says nothing was judged. The dashboard shows such a finding muted and keys on this
    /// text (a copy lives in the Admin project, which cannot reference this assembly; both are pinned by tests).
    /// </summary>
    internal const string NotJudgedPrefix = "Not checked yet";

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
        var candidate = Judge(validator, descriptorJson, segments, pointer, source);
        var atSlot = candidate.Findings.Where(f => JsonPointerPath.IsAtOrUnder(f.Path, pointer)).ToList();
        if (atSlot.Any(IsError) || candidate.ExpressionsJudged)
        {
            return atSlot;
        }

        var baseline = Judge(validator, descriptorJson, segments, pointer, Placeholder);

        return [.. atSlot, Explain(candidate.Findings, baseline.Findings, pointer)];
    }

    /// <summary>What a validator said, and whether its expression passes ran at all.</summary>
    private readonly record struct Outcome(IReadOnlyList<DescriptorValidationError> Findings, bool ExpressionsJudged);

    /// <summary>Splices <paramref name="source"/> into the descriptor and runs the validator apply runs.</summary>
    /// <remarks>
    /// The real <see cref="DescriptorValidator"/> says whether its rule pass ran. Any other
    /// <see cref="IDescriptorValidator"/> (a decorator, a fake) cannot say, so the schema pass's own mark stands in:
    /// its findings are the only ones written <c>#/…</c>, and none of them means the passes ran.
    /// </remarks>
    private static Outcome Judge(
        IDescriptorValidator validator, string descriptorJson, IReadOnlyList<string> segments, string pointer, string source)
    {
        try
        {
            var root = Parse(descriptorJson);
            Splice(root, segments, pointer, source);
            var json = root.ToJsonString();
            if (validator is DescriptorValidator real)
            {
                var (result, judged) = real.ValidateWithOutcome(json);

                return new Outcome(result.Errors, judged);
            }

            var findings = validator.Validate(json).Errors;

            return new Outcome(findings, !findings.Any(f => JsonPointerPath.IsSchemaPath(f.Path)));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("UTF-16", StringComparison.Ordinal))
        {
            // Parse, serialise and the validator all throw this for half a surrogate pair: a request that cannot be
            // answered (422), never a 500.
            throw new ManagementRequestException(
                "The 'descriptorJson' contains text that is not valid Unicode (a lone surrogate: half of a \\uD800-\\uDFFF pair). "
                + "Send the working-copy descriptor exactly as the dashboard holds it, with each such escape completed or written as the character.");
        }
    }

    private static bool IsError(DescriptorValidationError finding) => finding.Severity == DescriptorValidationSeverity.Error;

    /// <summary>The finding that stands in for the slot's own verdict, which the validator could not give.</summary>
    private static DescriptorValidationError Explain(
        IReadOnlyList<DescriptorValidationError> candidate, IReadOnlyList<DescriptorValidationError> baseline, string pointer)
    {
        if (HasSchemaError(candidate) && !HasSchemaError(baseline))
        {
            return CandidateRefused(candidate, pointer);
        }

        var elsewhere = ErrorPlaces(baseline, pointer);

        return NotJudged(elsewhere.Count > 0 ? elsewhere : ErrorPlaces(candidate, pointer), pointer);
    }

    private static bool HasSchemaError(IReadOnlyList<DescriptorValidationError> findings) =>
        findings.Any(f => JsonPointerPath.IsSchemaPath(f.Path));

    /// <summary>
    /// The one error that says nothing was judged, when the descriptor is refused somewhere <b>else</b>: the
    /// validator runs its rule, computed and owner passes only when the schema accepts the descriptor and the mapper
    /// builds it, so a slot's own errors are not reported while either fails. A pass here would be a pass for
    /// something nobody looked at.
    /// </summary>
    private static DescriptorValidationError NotJudged(IReadOnlyList<string> elsewhere, string pointer) => new(
        pointer,
        $"{NotJudgedPrefix} — another part of this draft is not valid ({string.Join(", ", elsewhere)}). "
        + "This box is checked once that is fixed.",
        "Fix the part named; Apply lists every problem.",
        DescriptorValidationSeverity.Error);

    /// <summary>
    /// A schema refusal the validator reports on a node <b>above</b> the slot (a mutate target is a <c>oneOf</c>, so
    /// its value fails on the action), caused by the candidate: the descriptor is schema-valid with a placeholder in it.
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

    /// <summary>Up to three places outside the slot where the descriptor is refused; an ancestor of another is only its echo.</summary>
    private static List<string> ErrorPlaces(IReadOnlyList<DescriptorValidationError> findings, string pointer)
    {
        var places = findings
            .Where(f => IsError(f) && !JsonPointerPath.IsAtOrUnder(f.Path, pointer))
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

            var root = JsonNode.Parse(descriptorJson, documentOptions: options) as JsonObject ?? throw NotADescriptor();
            EnsureArraysBounded(root);

            return root;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ? DuplicateProperty(ex) : NotADescriptor();
        }
    }

    /// <summary>
    /// Refuses an array longer than <see cref="MaxArrayLength"/>. The schema is frozen and a descriptor of 2,000
    /// <c>enum</c> values takes the validator about 0.1 s while 90,000 take minutes, all under the size cap: a check runs
    /// on every keystroke for a Viewer, so the bound is the check's own and walks the tree once, iteratively.
    /// </summary>
    private static void EnsureArraysBounded(JsonNode root)
    {
        var pending = new Stack<(JsonNode Node, string Pointer)>([(root, string.Empty)]);
        while (pending.Count > 0)
        {
            var (node, pointer) = pending.Pop();
            switch (node)
            {
                case JsonArray array when array.Count > MaxArrayLength:
                    throw TooLong(pointer);
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        PushChild(pending, array[i], $"{pointer}/{i}");
                    }

                    break;
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        PushChild(pending, child, $"{pointer}/{key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}");
                    }

                    break;
            }
        }
    }

    private static void PushChild(Stack<(JsonNode Node, string Pointer)> pending, JsonNode? child, string pointer)
    {
        if (child is JsonArray or JsonObject)
        {
            pending.Push((child, pointer));
        }
    }

    private static ManagementRequestException TooLong(string pointer) => new(
        $"The array at '{JsonPointerPath.Shorten(pointer)}' has more than {MaxArrayLength:N0} elements. A check is for one "
        + "expression; send the descriptor as written — an array this long is not expected.");

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
        "The 'descriptorJson' is not a JSON object. Send the working-copy descriptor exactly as the dashboard holds it.");

    /// <summary>The parser's own text names the property and its position; the client needs both to find it.</summary>
    private static ManagementRequestException DuplicateProperty(Exception parserError) => new(
        $"The 'descriptorJson' has a duplicate property ({parserError.Message}). JSON objects must not repeat a key; "
        + "send the working-copy descriptor exactly as the dashboard holds it.");

    private static ManagementRequestException Absent(string pointer) => new(
        $"'{JsonPointerPath.Shorten(pointer)}' does not exist in the descriptor sent. The slot must already be in the descriptor — add the "
        + "rule or hook to the working copy first, then check the expression in it.");
}
