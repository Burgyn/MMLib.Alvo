using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the hook editor holds, and the action it builds from it in the schema's own shape — for a new hook, or for a declared
/// one it opened (<see cref="From"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A before-hook and an after-hook are not the same editor.</b> The frozen schema's
/// <c>$defs/beforeHookList</c> admits <c>reject</c> and <c>mutate</c> only — a before-hook runs inside the
/// write's own transaction, with no network — and an after-hook is where a webhook or an email belongs. The
/// kinds a point admits are therefore decided by the point, and switching the point moves the kind with it.
/// </para>
/// <para>
/// <b>An edit is a patch</b> (<see cref="BuildHook"/>, <see cref="HookPatch"/>): what the form changed, written onto the
/// hook it opened. A mutate patches any number of fields, each with a literal or a CEL value (spec §4.3).
/// </para>
/// <para>
/// <b>Out of the component so it can be tested</b> (docs/architecture/admin-dashboard-review.md, F-13).
/// </para>
/// </remarks>
internal sealed partial class HookBuilder
{
    /// <summary>The kind that refuses the write.</summary>
    public const string Reject = "reject";

    /// <summary>The kind that patches the row.</summary>
    public const string Mutate = "mutate";

    /// <summary>The kind that delivers to a declared endpoint.</summary>
    public const string Webhook = "webhook";

    /// <summary>The kind that sends a templated email.</summary>
    public const string Email = "email";

    /// <summary>The most characters a payload holds (schema <c>$defs/jsonata</c>).</summary>
    public const int MaxPayloadLength = 8000;

    private static readonly string[] _beforeKinds = [Reject, Mutate];
    private static readonly string[] _deleteKinds = [Reject];
    private static readonly string[] _afterKinds = [Webhook, Email];
    private static readonly IReadOnlyDictionary<string, FieldSchema> _noFields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>The six hook points, in the order a write passes them.</summary>
    public static IReadOnlyList<string> Points { get; } =
        ["beforeCreate", "beforeUpdate", "beforeDelete", "afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>The point the hook is declared at.</summary>
    public string Point { get; private set; } = Points[0];

    /// <summary>Whether the point is fixed — an opened hook keeps its place (spec D1).</summary>
    public bool PointLocked { get; private set; }

    /// <summary>The action's kind; one of <see cref="Kinds"/>.</summary>
    public string Kind { get; set; } = Reject;

    /// <summary>The CEL guard, or empty for a hook that always runs.</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>A reject's message, which the caller reads as the problem's detail.</summary>
    public string RejectMessage { get; set; } = string.Empty;

    /// <summary>The fields a mutate patches, in the order they are written; empty until the form adds one.</summary>
    public List<MutateRow> MutateRows { get; } = [];

    /// <summary>A webhook's endpoint, by its declared name.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>A webhook's <c>{{…}}</c> payload, or empty for the CloudEvents envelope.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>An email's template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Who an email goes to.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>The entity's fields as the working copy declares them, which a mutate literal is converted for.</summary>
    public IReadOnlyDictionary<string, FieldSchema> Fields { get; set; } = _noFields;

    /// <summary>
    /// The action kinds this point admits.
    /// </summary>
    /// <remarks>
    /// <b>Three answers, not two, and the third does not come from the schema.</b> <c>$defs/beforeHookList</c>
    /// is one definition for all three before-points, so the schema alone would offer <c>mutate</c> at
    /// <c>beforeDelete</c> — which <c>BeforeHookCompiler</c> refuses outright, because the row is being
    /// removed and a patch against it is a slot that compiles cleanly and is then discarded.
    /// </remarks>
    public IReadOnlyList<string> Kinds => Point switch
    {
        "beforeDelete" => _deleteKinds,
        _ => IsBefore(Point) ? _beforeKinds : _afterKinds,
    };

    /// <summary>
    /// The row images a condition at this point may name, as markup.
    /// </summary>
    /// <remarks>
    /// <b>Guidance that named both at every point was guidance to write a refused descriptor.</b> A create
    /// has no pre-image and a delete produces no post-image, so <c>old.</c> under a <c>beforeCreate</c> and
    /// <c>new.</c> under a <c>beforeDelete</c> are references the phase cannot answer — and
    /// <c>BeforeHookCompiler</c> refuses both, because an unanswerable reference resolves to null and the
    /// null rule collapses every comparison against it, including <c>!=</c>.
    /// </remarks>
    public string Images => ConditionTable.ImagesMarkup(Point);

    /// <summary>A condition that is valid at this point, for the placeholder.</summary>
    public string Example => ConditionTable.Example(Point);

    /// <summary>Whether anything has been typed — what closing a new hook's editor would lose.</summary>
    /// <remarks>The point and the kind are not counted, for the reason <c>HooksTab.Dirty</c> gives.</remarks>
    public bool HasInput => Condition.Length > 0 || RejectMessage.Length > 0 || Endpoint.Length > 0 || Payload.Length > 0
        || Template.Length > 0 || To.Length > 0
        || MutateRows.Any(row => row.Field.Length > 0 || row.Text.Length > 0 || row.Empty);

    /// <summary>Whether a point runs inside the write's transaction.</summary>
    public static bool IsBefore(string point)
        => point.StartsWith("before", StringComparison.Ordinal);

    /// <summary>
    /// Switches the point, and the action kind with it when the current one no longer fits — unless the point is fixed.
    /// </summary>
    /// <remarks>
    /// Moving from <c>afterCreate</c> to <c>beforeCreate</c> with <c>webhook</c> still selected would leave
    /// the editor holding a descriptor the apply refuses — a before-hook may not touch the network — and the
    /// refusal would name a choice the operator never made.
    /// </remarks>
    public void Choose(string point)
    {
        if (PointLocked)
        {
            return;
        }

        Point = point;
        if (!Kinds.Contains(Kind))
        {
            Kind = Kinds[0];
        }
    }

    /// <summary>Everything the form holds, as one string: an edit is dirty while this differs from what it opened with.</summary>
    public string Fingerprint() => JsonSerializer.Serialize(new
    {
        Point,
        Kind,
        Condition,
        RejectMessage,
        Endpoint,
        Payload,
        Template,
        To,
        Rows = MutateRows.Select(row => new { row.Field, Mode = row.Mode.ToString(), row.Text, row.Empty }),
    });

    /// <summary>
    /// The action in the schema's shape, or why it cannot be built.
    /// </summary>
    /// <remarks>
    /// The schema's own <c>required</c> per kind, and for a mutate the rows' own refusals: a field named twice, a field the
    /// working copy does not declare, a literal its type cannot hold. Whether the CEL compiles, or the endpoint or the
    /// template exists, is the apply's — and the live check's.
    /// </remarks>
    /// <param name="refusal">Why the action cannot be built, when it cannot.</param>
    /// <returns>The action, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
    public JsonObject? Build(out string? refusal)
    {
        refusal = Missing() ?? (Kind == Mutate ? MutateRefusal() : null);
        return refusal is null ? Action() : null;
    }

    /// <summary>The whole hook to write: the form patched onto <paramref name="original"/>, or a new hook.</summary>
    /// <param name="original">The declared hook the editor opened, or <see langword="null"/> for a new one.</param>
    /// <param name="refusal">Why it cannot be built, when it cannot.</param>
    /// <returns>The hook, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
    public JsonObject? BuildHook(JsonObject? original, out string? refusal)
        => Build(out refusal) is { } action ? HookPatch.Apply(original, Condition, action) : null;

    /// <summary>
    /// The action the form would add, or — while it cannot be built yet — the same kind with the smallest valid stand-in
    /// for each facet still blank.
    /// </summary>
    /// <remarks>
    /// For the live expression check, which asks about one box before the rest of the form is filled in: the answer is
    /// filtered to that box's own slot, so the stand-ins are never judged. Never staged; <see cref="Build"/> is the way in.
    /// </remarks>
    /// <returns>The action in the schema's shape.</returns>
    public JsonObject Draft() => Build(out _) ?? Standing().Action();

    /// <summary>The hook an action slot's check is asked about: the draft patched onto the original, without the condition.</summary>
    /// <remarks>
    /// An action slot is checked without the condition (spec D4): <c>AfterHookCompiler.CompileHook</c> skips the action when
    /// the condition fails, so a broken condition would otherwise read as a green payload. The condition box builds its own
    /// candidate from the text it was asked about.
    /// </remarks>
    /// <param name="original">The declared hook the editor opened, or <see langword="null"/>; not modified.</param>
    /// <returns>The candidate hook.</returns>
    public JsonObject CandidateHook(JsonObject? original) => HookPatch.Apply(original, condition: null, Draft());

    /// <summary>Empties what was typed, and keeps the point and the kind for the next hook.</summary>
    public void Clear()
    {
        Condition = string.Empty;
        RejectMessage = string.Empty;
        Endpoint = string.Empty;
        Payload = string.Empty;
        Template = string.Empty;
        To = string.Empty;
        MutateRows.Clear();
    }

    /// <summary>The schema's required facet the kind is missing, or nothing.</summary>
    /// <remarks>
    /// Anything that is not one of the first three is built as an email, so it is refused as one: a kind
    /// this editor does not know can never build an email with no template and no recipient.
    /// </remarks>
    private string? Missing() => Kind switch
    {
        Reject => Blank(RejectMessage) ? "A reject carries the message the caller reads. Write one." : null,
        Mutate => MutateRows.Count == 0 || MutateRows.Any(IsIncomplete)
            ? "A mutate patches at least one field. Name the field and what to set it to."
            : null,
        Webhook => Blank(Endpoint) ? "A webhook names an endpoint declared under 'webhooks.endpoints'." : null,
        _ => Blank(Template) || Blank(To) ? "An email names a template and who it goes to." : null,
    };

    private static bool IsIncomplete(MutateRow row) => Blank(row.Field) || (row.Mode == MutateMode.Expression && Blank(row.Text));

    private string? MutateRefusal()
    {
        var twice = MutateRows.GroupBy(row => row.Field.Trim(), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        return twice is not null
            ? $"'{twice.Key}' is patched twice. Keep one row per field."
            : MutateRows.Select(RowRefusal).FirstOrDefault(refusal => refusal is not null);
    }

    private string? RowRefusal(MutateRow row)
    {
        var name = row.Field.Trim();
        if (!Fields.TryGetValue(name, out var field))
        {
            return $"'{name}' is not a field of this entity in the working copy, so this mutate could never be written.";
        }

        return row.Mode == MutateMode.Literal && !MutateLiteral.TryValue(row, field, out _, out var refusal) ? refusal : null;
    }

    private JsonObject Action() => Kind switch
    {
        Reject => new JsonObject { [Reject] = RejectMessage },
        Mutate => new JsonObject { [Mutate] = Patch() },
        Webhook => WebhookAction(),
        _ => new JsonObject { ["type"] = Email, ["template"] = Template, ["to"] = To },
    };

    private JsonObject WebhookAction()
    {
        var action = new JsonObject { ["type"] = Webhook, ["endpoint"] = Endpoint };
        if (!Blank(Payload))
        {
            action["payload"] = Payload;
        }

        return action;
    }

    private JsonObject Patch()
    {
        var patch = new JsonObject();
        foreach (var row in MutateRows.Where(row => !Blank(row.Field)))
        {
            patch[row.Field.Trim()] = ValueOf(row);
        }

        return patch;
    }

    private JsonNode? ValueOf(MutateRow row)
    {
        if (row.Mode == MutateMode.Expression)
        {
            return new JsonObject { ["$cel"] = row.Text };
        }

        return Fields.TryGetValue(row.Field.Trim(), out var field) && MutateLiteral.TryValue(row, field, out var value, out _)
            ? value
            : JsonValue.Create(row.Text);
    }

    /// <summary>The same form with a stand-in for every blank the schema requires, for <see cref="Draft"/>.</summary>
    private HookBuilder Standing()
    {
        var stand = new HookBuilder
        {
            Kind = Kind,
            RejectMessage = Stand(RejectMessage),
            Endpoint = Stand(Endpoint),
            Payload = Payload,
            Template = Stand(Template),
            To = Stand(To),
            Fields = Fields,
        };
        stand.MutateRows.AddRange(MutateRows.Where(row => !Blank(row.Field)).Select(row =>
            new MutateRow(row.Field, row.Mode, row.Mode == MutateMode.Expression ? Stand(row.Text) : row.Text) { Empty = row.Empty }));
        if (stand.MutateRows.Count == 0)
        {
            stand.MutateRows.Add(new MutateRow("x", MutateMode.Expression, "x"));
        }

        return stand;
    }

    private static string Stand(string value) => Blank(value) ? "x" : value;

    private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
}
