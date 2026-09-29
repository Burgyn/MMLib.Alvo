using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>The graders of the seven skill cases (spec §7.5, D36).</summary>
/// <remarks>
/// <para>
/// A hook is read from the proposed slot, and a hook path is accepted as <c>/entities/X/hooks</c> itself or anything
/// under it (ruling B4): an entity without hooks gains the whole block, an entity with hooks gains one slot or one
/// entry, and the right answer may take any of the three shapes.
/// </para>
/// <para>
/// A CEL text is compared in one normal form (<see cref="Normal"/>): whitespace outside literals dropped, double quotes
/// read as single, so <c>"admin"</c> and <c>'admin'</c> are one grant. Every clause has a wrong-turn fact in
/// <c>SkillCasesTests</c> that fails when the clause is removed.
/// </para>
/// </remarks>
internal static partial class EvalCases
{
    private const string Capabilities = "get_capabilities";
    private const string RentalsHooks = "/entities/rentals/hooks";
    private const string PartsHooks = "/entities/parts/hooks";
    private const string OrderLines = "/entities/order_lines";
    private const string OrderLinesIndexes = $"{OrderLines}/indexes";
    private const string ServiceOrdersRules = "/entities/service_orders/rules";
    private const string ReadsTheAssignee = "assigned_user_id==@user.id";
    private const string BecomesReturned = "new.status=='returned'";
    private static readonly string[] _priceSlots = ["beforeCreate", "beforeUpdate"];
    private static readonly string[] _keptGrants = ["'admin'in@user.roles", "'manager'in@user.roles", ReadsTheAssignee];
    private static readonly string[] _uniquePair = ["order_id", "part_id"];
    private static readonly string[] _readRules = [$"{ServiceOrdersRules}/get", $"{ServiceOrdersRules}/list"];
    private static readonly string[] _wasNotReturned = ["old.status!='returned'", "changed(status)"];

    /// <summary>A <c>beforeUpdate</c> on rentals that stamps <c>returned_at</c> with <c>now()</c> when the status becomes returned.</summary>
    /// <remarks>"Becomes" is both halves: the new status is returned, and it was not, or it changed.</remarks>
    private static Verdict HookReturnedAt(TurnRecord turn)
    {
        var stamps = Entries(turn.Proposed($"{RentalsHooks}/beforeUpdate")).Any(hook =>
            Condition(hook).Contains(BecomesReturned, StringComparison.Ordinal) && _wasNotReturned.Any(Condition(hook).Contains)
            && Normal(TextAt(hook, "action", "mutate", "returned_at", "$cel")) == "now()");
        return Verdict.When(
            turn.HasValidProposal && OnlyUnder(turn, RentalsHooks) && stamps,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] stampsReturnedAt={stamps}");
    }

    /// <summary>
    /// Only parts' hooks change, with a <c>reject</c> of a negative <c>unit_price</c> in both <c>beforeCreate</c> and
    /// <c>beforeUpdate</c> — hooks are the only way this build keeps a price non-negative, since a field's
    /// <c>validation</c> is refused, and a <c>validation</c> added beside them is a change outside the hooks.
    /// </summary>
    private static Verdict RejectNegativePrice(TurnRecord turn)
    {
        var guarded = _priceSlots.Where(slot => Entries(turn.Proposed($"{PartsHooks}/{slot}"))
            .Any(hook => NegativePrice().IsMatch(Condition(hook)) && hook["action"]?["reject"] is JsonValue)).ToList();
        return Verdict.When(
            turn.HasValidProposal && OnlyUnder(turn, PartsHooks) && guarded.Count == _priceSlots.Length,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] rejectsIn=[{Joined(guarded)}]");
    }

    /// <summary>
    /// Exactly one change, a new field on customers holding a <c>count</c> rollup from rentals — so a hook maintaining
    /// a counter beside it, the ladder's wrong rung, is a second change and fails.
    /// </summary>
    private static Verdict RollupRentalsCount(TurnRecord turn)
    {
        var field = turn.ChangedPaths is [var only] && only.StartsWith($"{Customers}/", StringComparison.Ordinal) ? turn.Proposed(only) : null;
        var counts = TextAt(field, "rollup", "from") == "rentals" && TextAt(field, "rollup", "op") == "count";
        return Verdict.When(
            turn.HasValidProposal && counts,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] countsRentals={counts}");
    }

    /// <summary>
    /// Only order_lines' indexes change, one of them unique over exactly <c>order_id</c> and <c>part_id</c> — so a
    /// <c>part_id</c> made unique across the table is a change outside the indexes and fails.
    /// </summary>
    private static Verdict UniquePartPerOrder(TurnRecord turn)
    {
        var pair = Entries(turn.Proposed(OrderLinesIndexes)).Any(index => index["unique"] is JsonValue unique
            && unique.TryGetValue<bool>(out var on) && on
            && Texts(index["fields"]).Order(StringComparer.Ordinal).SequenceEqual(_uniquePair));
        return Verdict.When(
            turn.HasValidProposal && OnlyUnder(turn, OrderLinesIndexes) && pair,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] uniquePair={pair}");
    }

    /// <summary>
    /// <c>rules.list</c> and <c>rules.get</c>, and nothing else, read the assignee and keep the admin and manager grants;
    /// neither still admits every authenticated caller.
    /// </summary>
    private static Verdict OwnOrdersOnly(TurnRecord turn)
    {
        var rules = _readRules.ToDictionary(path => path[(ServiceOrdersRules.Length + 1)..], path => Normal(turn.ProposedText(path) ?? string.Empty));
        var missing = rules.SelectMany(rule => _keptGrants.Where(grant => !rule.Value.Contains(grant, StringComparison.Ordinal))
            .Select(grant => $"{rule.Key}:{grant}")).ToList();
        var everyone = rules.Where(rule => rule.Value.Contains("'authenticated'", StringComparison.Ordinal)).Select(rule => rule.Key).ToList();
        return Verdict.When(
            turn.HasValidProposal && turn.ChangedPaths.Order(StringComparer.Ordinal).SequenceEqual(_readRules) && missing.Count == 0 && everyone.Count == 0,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] missing=[{Joined(missing)}] keepsEveryone=[{Joined(everyone)}]");
    }

    /// <summary>
    /// Nothing proposed touches a hook — a <c>function</c> action is refused, and a <c>webhook</c> or <c>http.call</c>
    /// substitute is not what was asked — the capability report was read, and the turn answered.
    /// </summary>
    private static Verdict FunctionActionRefused(TurnRecord turn)
    {
        var hooks = turn.ChangedPaths.Any(path => path.Contains("/hooks", StringComparison.Ordinal));
        var read = turn.ToolCalls.Contains(Capabilities);
        return Verdict.When(
            !hooks && read && turn.Answer.Trim().Length > 0,
            $"proposesHook={hooks} readCapabilities={read}");
    }

    /// <summary>An answer only: no proposal, the capability report read, and no claim that Alvo can make the call.</summary>
    /// <remarks>
    /// The claim is read at the start of the model's own prose with emphasis stripped: a leading yes or áno, or
    /// "Alvo / it can", "vie", "dokáže". A negative answer ("No, …", "Nie, …") is never a claim.
    /// </remarks>
    private static Verdict CanAlvoCallHttp(TurnRecord turn)
    {
        var prose = Emphasis().Replace(ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts), string.Empty).Trim().ToLowerInvariant();
        var claims = Affirmative().IsMatch(prose);
        var read = turn.ToolCalls.Contains(Capabilities);
        return Verdict.When(
            turn.Proposal is null && read && !claims && prose.Length > 0,
            $"proposal={turn.Proposal is not null} readCapabilities={read} claimsCapability={claims}");
    }

    private static bool OnlyUnder(TurnRecord turn, string root) =>
        turn.ChangedPaths.Count > 0
        && turn.ChangedPaths.All(path => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

    private static IEnumerable<JsonObject> Entries(JsonNode? node) =>
        node is JsonArray items ? items.OfType<JsonObject>() : [];

    private static IEnumerable<string> Texts(JsonNode? node) =>
        node is JsonArray items ? items.Select(item => TextAt(item)) : [];

    private static string Condition(JsonObject hook) => Normal(TextAt(hook, "condition"));

    /// <summary>CEL in one form: no whitespace outside literals, double quotes as single, the assignee comparison either way round.</summary>
    private static string Normal(string cel) =>
        ReplyText.Compact(cel).Replace('"', '\'').Replace("@user.id==assigned_user_id", ReadsTheAssignee, StringComparison.Ordinal);

    private static string TextAt(JsonNode? node, params string[] members) =>
        members.Aggregate(node, (current, member) => current is JsonObject owner ? owner[member] : null) is JsonValue value
            && value.TryGetValue<string>(out var text) ? text : string.Empty;

    [GeneratedRegex(@"(?:(?:new\.)?unit_price<0(?:\.0+)?|0(?:\.0+)?>(?:new\.)?unit_price)(?![0-9.])", RegexOptions.CultureInvariant)]
    private static partial Regex NegativePrice();

    [GeneratedRegex(@"[*_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\A(?:(?:yes|áno|ano)\b|(?:(?:alvo|it)\s+)?(?:can|vie|dokáže|dokaze)\b(?!['’]t))", RegexOptions.CultureInvariant)]
    private static partial Regex Affirmative();
}
