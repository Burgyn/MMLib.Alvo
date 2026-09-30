using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>The RCA 2 case (spec §9, D53): tables for task management, from the turn that stopped with attempts left.</summary>
internal static partial class EvalCases
{
    private const string UsersEntity = "users";

    /// <summary>What the operator's request links a task to: the employee, the customer and the goods.</summary>
    private static readonly string[] _taskLinks = ["technicians", "customers", "parts"];

    private static readonly string[] _beforeHookPoints = ["beforeCreate", "beforeUpdate", "beforeDelete"];

    /// <summary>
    /// One valid proposal of at least two new entities and nothing else, linked to technicians, customers and parts,
    /// every ref to a declared entity or <c>users</c>, no managed column declared, no rule or before-hook condition
    /// comparing <c>@user.id</c> with a ref to another entity, and at most one refused attempt.
    /// </summary>
    /// <remarks>
    /// The request is open ("a discussion"), so two new entities is a floor, not a count. A ref to an undeclared entity
    /// and a managed column are graded although validity implies them (D16's reasoning): a validator that stopped
    /// refusing either still fails the case. The skills and the word "proposed" are graded on every turn by
    /// <see cref="SkillsRead"/> and <see cref="ProposalWording"/>, so they are not repeated here.
    /// </remarks>
    private static Verdict TaskManagementWorkers(TurnRecord turn)
    {
        var original = JsonNode.Parse(turn.OriginalDescriptor);
        var created = CreatedEntities(turn, original);
        var targets = created.SelectMany(entity => RefTargets(entity.Value)).ToHashSet(StringComparer.Ordinal);
        var known = KnownEntities(original, created);
        var grade = new TaskManagementGrade(
            turn.HasValidProposal,
            turn.RefusedAttempts,
            created.Count,
            OnlyNewEntities: created.Count == turn.ChangedPaths.Count,
            Links: _taskLinks.All(targets.Contains),
            Undeclared: [.. targets.Where(target => !known.Contains(target)).Order(StringComparer.Ordinal)],
            Managed: [.. created.SelectMany(entity => DeclaredManagedColumns(entity.Value).Select(column => $"{entity.Key}.{column}"))],
            CallerVsRef: [.. created.SelectMany(entity => CallerComparedWithRef(entity.Value).Select(field => $"{entity.Key}.{field}"))]);
        return Verdict.When(grade.Passed, grade.Why);
    }

    /// <summary>Every changed path that adds a whole entity the original does not declare, with what it declares.</summary>
    private static List<KeyValuePair<string, JsonNode?>> CreatedEntities(TurnRecord turn, JsonNode? original) =>
    [
        .. turn.ChangedPaths
            .Where(path => EntityPointer().IsMatch(path) && DescriptorDiff.At(original, path) is null)
            .Select(path => new KeyValuePair<string, JsonNode?>(path["/entities/".Length..], turn.Proposed(path))),
    ];

    /// <summary>What a ref may target: an entity the original declares, one the proposal creates, or <c>users</c>.</summary>
    private static HashSet<string> KnownEntities(JsonNode? original, List<KeyValuePair<string, JsonNode?>> created) =>
        [
            .. (original?["entities"] as JsonObject ?? new JsonObject()).Select(entity => entity.Key),
            .. created.Select(entity => entity.Key),
            UsersEntity,
        ];

    private static IEnumerable<string> RefTargets(JsonNode? entity) =>
        FieldsOf(entity).Where(field => Text(field.Value, "type") == "ref").Select(field => Text(field.Value, "entity")).OfType<string>();

    /// <summary>
    /// The fields a rule or a before-hook condition compares with <c>@user.id</c> that ref an entity other than
    /// <c>users</c> — read from the CEL in its normal form, which drops whitespace and reads <c>"</c> as <c>'</c>.
    /// </summary>
    private static IEnumerable<string> CallerComparedWithRef(JsonNode? entity)
    {
        var refsElsewhere = FieldsOf(entity)
            .Where(field => Text(field.Value, "type") == "ref" && Text(field.Value, "entity") is { } target && target != UsersEntity)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);
        return CelSources(entity)
            .SelectMany(source => CallerComparison().Matches(Normal(source)))
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(refsElsewhere.Contains)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Every rule string and every before-hook condition an entity declares.</summary>
    private static IEnumerable<string> CelSources(JsonNode? entity)
    {
        var rules = (entity?["rules"] as JsonObject ?? new JsonObject()).Select(rule => TextAt(rule.Value));
        var conditions = _beforeHookPoints.SelectMany(point => Entries(entity?["hooks"]?[point]).Select(hook => TextAt(hook, "condition")));
        return rules.Concat(conditions).Where(source => source.Length > 0);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> FieldsOf(JsonNode? entity) =>
        entity?["fields"] as JsonObject ?? new JsonObject();

    /// <summary>An <c>==</c> or <c>!=</c> between <c>@user.id</c> and a field, in either order, over normal-form CEL.</summary>
    [GeneratedRegex(
        @"(?:new\.|old\.)?([a-z][a-z0-9_]*)(?:==|!=)@user\.id|@user\.id(?:==|!=)(?:new\.|old\.)?([a-z][a-z0-9_]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CallerComparison();

    /// <summary>What the task-management grade saw, clause by clause.</summary>
    private sealed record TaskManagementGrade(
        bool Valid,
        int Refused,
        int NewEntities,
        bool OnlyNewEntities,
        bool Links,
        IReadOnlyList<string> Undeclared,
        IReadOnlyList<string> Managed,
        IReadOnlyList<string> CallerVsRef)
    {
        internal bool Passed =>
            Valid && Refused <= 1 && NewEntities >= 2 && OnlyNewEntities && Links
            && Undeclared.Count == 0 && Managed.Count == 0 && CallerVsRef.Count == 0;

        internal string Why =>
            $"valid={Valid} refused={Refused} newEntities={NewEntities} onlyNewEntities={OnlyNewEntities} links={Links} "
            + $"undeclared=[{Joined(Undeclared)}] managed=[{Joined(Managed)}] callerVsRef=[{Joined(CallerVsRef)}]";
    }
}
