using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The relations among <c>list</c> rules, judged at apply: a cycle (a self-loop included) has no caller whose rows can
/// be decided, and a chain deeper than <see cref="MaxDepth"/> is refused at the rule that starts it. A rule of another
/// operation, or a lookup check, only consumes its targets' <c>list</c> rules, and is refused when its own chain would
/// pass the limit — so the binder never meets its runtime depth cap on a catalog that applied.
/// </summary>
internal static class RelationGraph
{
    /// <summary>The deepest relation chain a rule may read.</summary>
    internal const int MaxDepth = 3;

    private const string CycleFix =
        "Move the condition into one of the two rules, or replace one relation with a rollup or a stored field.";

    private const string TooDeepFix = "Read a stored field or a rollup on a nearer entity.";

    /// <summary>The entities a tree's relation terms read, each once, in the order they first appear.</summary>
    /// <param name="root">A compiled tree, or <see langword="null"/>.</param>
    internal static IReadOnlyList<string> Targets(CelNode? root)
    {
        var targets = new List<string>();
        if (root is not null)
        {
            CelRewriter.Any(root, node =>
            {
                if (node is CelRelation relation && !targets.Contains(relation.Related.Name, StringComparer.Ordinal))
                {
                    targets.Add(relation.Related.Name);
                }

                return false;
            });
        }

        return targets;
    }

    /// <summary>Refuses every list-rule cycle and every chain longer than <see cref="MaxDepth"/>.</summary>
    /// <param name="listReads">Each entity, and the entities its <c>list</c> rule reads.</param>
    /// <param name="consumers">Every other rule or check that reads a relation: where it is, and what it reads.</param>
    /// <param name="errors">The apply's error list.</param>
    internal static void Check(
        IReadOnlyDictionary<string, IReadOnlyList<string>> listReads,
        IReadOnlyList<RelationConsumer> consumers,
        List<DescriptorValidationError> errors)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in listReads.Keys.Order(StringComparer.Ordinal))
        {
            Visit(entity, [], listReads, depth, errors);
            RefuseTooDeep(new RelationConsumer(entity, "rules.list", ListPath(entity), listReads[entity]), depth[entity], errors);
        }

        foreach (var consumer in consumers)
        {
            RefuseTooDeep(consumer, consumer.Targets.Select(target => 1 + depth.GetValueOrDefault(target)).DefaultIfEmpty(0).Max(), errors);
        }
    }

    private static int Visit(
        string entity,
        List<string> path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> edges,
        Dictionary<string, int> depth,
        List<DescriptorValidationError> errors)
    {
        if (path.Contains(entity, StringComparer.Ordinal))
        {
            errors.Add(Cycle([.. path.SkipWhile(step => step != entity), entity]));
            return 0;
        }

        if (depth.TryGetValue(entity, out var known))
        {
            return known;
        }

        path.Add(entity);
        var deepest = edges.GetValueOrDefault(entity, []).Select(next => 1 + Visit(next, path, edges, depth, errors))
            .DefaultIfEmpty(0).Max();
        path.RemoveAt(path.Count - 1);
        depth[entity] = deepest;
        return deepest;
    }

    private static void RefuseTooDeep(RelationConsumer consumer, int levels, List<DescriptorValidationError> errors)
    {
        if (levels > MaxDepth)
        {
            errors.Add(new DescriptorValidationError(
                consumer.Path,
                $"'{consumer.Entity}.{consumer.Slot}' reads relations {levels} levels deep; at most {MaxDepth} are allowed.",
                TooDeepFix,
                DescriptorValidationSeverity.Error));
        }
    }

    private static DescriptorValidationError Cycle(IReadOnlyList<string> loop) => new(
        ListPath(loop[0]),
        $"Rule cycle: the list rules of {string.Join(" → ", loop.Select(entity => $"'{entity}'"))} read each other, so no "
        + "caller's rows can be decided.",
        CycleFix,
        DescriptorValidationSeverity.Error);

    private static string ListPath(string entity) => $"/entities/{entity}/rules/list";
}

/// <summary>A rule or check that reads relations: its entity, its slot as a refusal names it, its pointer, and its targets.</summary>
/// <param name="Entity">The entity the rule or check belongs to.</param>
/// <param name="Slot">The slot as the refusal names it: <c>rules.get</c>, <c>checks.0</c>.</param>
/// <param name="Path">The JSON pointer a refusal lands at.</param>
/// <param name="Targets">The entities its relation terms read.</param>
internal sealed record RelationConsumer(string Entity, string Slot, string Path, IReadOnlyList<string> Targets);
