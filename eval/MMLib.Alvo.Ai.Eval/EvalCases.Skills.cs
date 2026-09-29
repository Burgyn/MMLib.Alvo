using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>The graders of the seven skill cases (spec §7.5, D36).</summary>
/// <remarks>
/// A hook is read from the proposed slot, and a hook path is accepted as <c>/entities/X/hooks</c> itself or anything
/// under it (ruling B4): an entity without hooks gains the whole block, an entity with hooks gains one slot or one
/// entry, and the right answer may take any of the three shapes.
/// </remarks>
internal static partial class EvalCases
{
    private const string Capabilities = "get_capabilities";
    private const string RentalsHooks = "/entities/rentals/hooks";
    private const string PartsHooks = "/entities/parts/hooks";
    private const string OrderLines = "/entities/order_lines";
    private const string ServiceOrdersRules = "/entities/service_orders/rules";
    private const string ReadsTheAssignee = "assigned_user_id==@user.id";
    private static readonly string[] _priceSlots = ["beforeCreate", "beforeUpdate"];
    private static readonly string[] _keptGrants = ["'admin'in@user.roles", "'manager'in@user.roles", ReadsTheAssignee];
    private static readonly string[] _uniquePair = ["order_id", "part_id"];
    private static readonly string[] _readRules = [$"{ServiceOrdersRules}/get", $"{ServiceOrdersRules}/list"];
    private static readonly string[] _affirmatives = ["yes, ", "yes. ", "áno, ", "áno. ", "it can ", "dokáže ", "alvo can "];
    private static readonly string[] _statusBecomesReturned = ["new.status=='returned'", "changed(status)"];

    /// <summary>A <c>beforeUpdate</c> on rentals that stamps <c>returned_at</c> with <c>now()</c> when the status becomes returned.</summary>
    private static Verdict HookReturnedAt(TurnRecord turn)
    {
        var hooks = Entries(turn.Proposed($"{RentalsHooks}/beforeUpdate"));
        var stamps = hooks.Any(hook => _statusBecomesReturned.Any(Condition(hook).Contains)
            && ReplyText.Compact(TextAt(hook, "action", "mutate", "returned_at", "$cel")) == "now()");
        return Verdict.When(
            turn.HasValidProposal && OnlyUnder(turn, RentalsHooks) && stamps,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] stampsReturnedAt={stamps}");
    }

    /// <summary>A <c>reject</c> before-hook on parts over <c>unit_price</c>, and no <c>validation</c>, which is refused.</summary>
    private static Verdict RejectNegativePrice(TurnRecord turn)
    {
        var rejects = _priceSlots.SelectMany(slot => Entries(turn.Proposed($"{PartsHooks}/{slot}")))
            .Any(hook => Condition(hook).Contains("unit_price", StringComparison.Ordinal) && hook["action"]?["reject"] is JsonValue);
        var validation = turn.ChangedPaths.Any(path => path.EndsWith("/validation", StringComparison.Ordinal));
        return Verdict.When(
            turn.HasValidProposal && OnlyUnder(turn, PartsHooks) && rejects && !validation,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] rejectsPrice={rejects} validation={validation}");
    }

    /// <summary>One new field on customers: a <c>count</c> rollup from rentals, and no hook maintaining a counter.</summary>
    private static Verdict RollupRentalsCount(TurnRecord turn)
    {
        var field = turn.ChangedPaths is [var only] && only.StartsWith($"{Customers}/", StringComparison.Ordinal) ? turn.Proposed(only) : null;
        var counts = TextAt(field, "rollup", "from") == "rentals" && TextAt(field, "rollup", "op") == "count";
        var hooks = turn.ChangedPaths.Any(path => path.Contains("/hooks", StringComparison.Ordinal));
        return Verdict.When(
            turn.HasValidProposal && counts && !hooks,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] countsRentals={counts} hooks={hooks}");
    }

    /// <summary>A composite unique index over exactly <c>order_id</c> and <c>part_id</c>, and <c>part_id</c> not unique alone.</summary>
    private static Verdict UniquePartPerOrder(TurnRecord turn)
    {
        var pair = Entries(turn.Proposed($"{OrderLines}/indexes")).Any(index => index["unique"] is JsonValue unique
            && unique.TryGetValue<bool>(out var on) && on
            && Texts(index["fields"]).Order(StringComparer.Ordinal).SequenceEqual(_uniquePair));
        var alone = turn.Proposed($"{OrderLines}/fields/part_id/unique")?.ToJsonString() == "true";
        return Verdict.When(
            turn.HasValidProposal && pair && !alone,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] uniquePair={pair} partIdUnique={alone}");
    }

    /// <summary><c>rules.list</c> and <c>rules.get</c> read the assignee, and keep the admin and manager grants.</summary>
    private static Verdict OwnOrdersOnly(TurnRecord turn)
    {
        var missing = _readRules.SelectMany(path => _keptGrants.Where(grant => !ReplyText.Compact(turn.ProposedText(path) ?? string.Empty)
            .Contains(grant, StringComparison.Ordinal)).Select(grant => $"{path[(ServiceOrdersRules.Length + 1)..]}:{grant}")).ToList();
        return Verdict.When(
            turn.HasValidProposal && turn.ChangedPaths.Order(StringComparer.Ordinal).SequenceEqual(_readRules) && missing.Count == 0,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] missing=[{Joined(missing)}]");
    }

    /// <summary>No proposal carries a <c>function</c> action, the capability report was read, and the turn answered.</summary>
    private static Verdict FunctionActionRefused(TurnRecord turn)
    {
        var function = turn.Proposal is { } proposal && CarriesAction(JsonNode.Parse(proposal.DescriptorJson), "function");
        var read = turn.ToolCalls.Contains(Capabilities);
        return Verdict.When(
            !function && read && turn.Answer.Trim().Length > 0,
            $"proposesFunction={function} readCapabilities={read}");
    }

    /// <summary>An answer only: no proposal, the capability report read, and no claim that Alvo can make the call.</summary>
    private static Verdict CanAlvoCallHttp(TurnRecord turn)
    {
        var prose = ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts).TrimStart('>', ' ', '\n', '*').ToLowerInvariant();
        var claims = _affirmatives.Any(affirmative => prose.StartsWith(affirmative, StringComparison.Ordinal));
        var read = turn.ToolCalls.Contains(Capabilities);
        return Verdict.When(
            turn.Proposal is null && read && !claims && prose.Length > 0,
            $"proposal={turn.Proposal is not null} readCapabilities={read} claimsCapability={claims}");
    }

    private static bool OnlyUnder(TurnRecord turn, string hooks) =>
        turn.ChangedPaths.Count > 0
        && turn.ChangedPaths.All(path => path == hooks || path.StartsWith(hooks + "/", StringComparison.Ordinal));

    private static bool CarriesAction(JsonNode? descriptor, string type) =>
        (descriptor?["entities"] as JsonObject ?? []).Any(entity => (entity.Value?["hooks"] as JsonObject ?? [])
            .Where(slot => slot.Key.StartsWith("after", StringComparison.Ordinal))
            .SelectMany(slot => Entries(slot.Value)).Any(hook => TextAt(hook, "action", "type") == type));

    private static IEnumerable<JsonObject> Entries(JsonNode? node) =>
        node is JsonArray items ? items.OfType<JsonObject>() : [];

    private static IEnumerable<string> Texts(JsonNode? node) =>
        node is JsonArray items ? items.Select(item => TextAt(item)) : [];

    private static string Condition(JsonObject hook) => ReplyText.Compact(TextAt(hook, "condition"));

    private static string TextAt(JsonNode? node, params string[] members) =>
        members.Aggregate(node, (current, member) => current is JsonObject owner ? owner[member] : null) is JsonValue value
            && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
