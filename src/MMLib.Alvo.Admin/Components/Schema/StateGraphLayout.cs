using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>An entity's state machine laid out for the States tab: its states as boxes, its transitions as edges.</summary>
/// <param name="Nodes">Every state, plus a <c>*</c> node when a transition leaves any state.</param>
/// <param name="Edges">One per source of each transition, in declaration order.</param>
/// <param name="Width">The drawing's width.</param>
/// <param name="Height">The drawing's height.</param>
internal sealed record StateGraph(IReadOnlyList<StateNode> Nodes, IReadOnlyList<StateEdge> Edges, double Width, double Height);

/// <summary>One state's box.</summary>
/// <param name="State">The state, or <c>*</c> for any state.</param>
/// <param name="X">The box's left edge.</param>
/// <param name="Y">The box's top edge.</param>
/// <param name="Initial">Whether every create stores this state.</param>
/// <param name="Any">Whether this is the <c>*</c> node.</param>
internal sealed record StateNode(string State, double X, double Y, bool Initial, bool Any);

/// <summary>One source of one transition.</summary>
/// <param name="Transition">The transition's name.</param>
/// <param name="From">The state it leaves, or <c>*</c>.</param>
/// <param name="To">The state it enters.</param>
/// <param name="Gated">Whether it names roles or a condition, so not every caller the update rule admits may take it.</param>
internal sealed record StateEdge(string Transition, string From, string To, bool Gated);

/// <summary>
/// Lays a state machine out left to right: a state's column is its distance from the initial state (or the first value),
/// a state no transition reaches takes the column after the last, and states sharing a column stack downwards.
/// </summary>
/// <remarks>
/// Server-side and deterministic, like the system map's layout: the same machine draws the same picture before the circuit
/// connects and on every render. The <c>*</c> node sits in the first column, under the starting state.
/// </remarks>
internal static class StateGraphLayout
{
    /// <summary>The <c>from</c> that means any state.</summary>
    internal const string AnyState = "*";

    /// <summary>A box's width.</summary>
    internal const double NodeWidth = 120;

    /// <summary>A box's height.</summary>
    internal const double NodeHeight = 36;

    private const double ColumnStep = 160;

    private const double RowStep = 72;

    /// <summary>The machine's layout over the state field's declared <paramref name="values"/>.</summary>
    /// <param name="machine">The state machine.</param>
    /// <param name="values">The state field's enum values, in declaration order.</param>
    internal static StateGraph For(EntityStateMachine machine, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(values);

        var edges = Edges(machine);
        var states = values.Concat(edges.SelectMany(edge => new[] { edge.From, edge.To })).Where(state => state != AnyState)
            .Distinct(StringComparer.Ordinal).ToList();
        var ranks = Ranks(machine.Initial ?? states.FirstOrDefault(), states, edges);
        var nodes = Place(states, ranks, machine.Initial, edges.Any(edge => edge.From == AnyState));

        return new StateGraph(
            nodes,
            edges,
            nodes.Count == 0 ? 0 : nodes.Max(node => node.X) + NodeWidth,
            nodes.Count == 0 ? 0 : nodes.Max(node => node.Y) + NodeHeight);
    }

    private static List<StateEdge> Edges(EntityStateMachine machine) =>
    [
        .. from transition in machine.Transitions
           from source in transition.Value.From
           select new StateEdge(transition.Key, source, transition.Value.To, transition.Value.Roles is not null || transition.Value.When is not null),
    ];

    private static Dictionary<string, int> Ranks(string? start, List<string> states, List<StateEdge> edges)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (start is not null)
        {
            ranks[start] = 0;
            queue.Enqueue(start);
        }

        while (queue.TryDequeue(out var state))
        {
            foreach (var next in edges.Where(edge => edge.From == state || edge.From == AnyState).Select(edge => edge.To))
            {
                if (ranks.TryAdd(next, ranks[state] + 1))
                {
                    queue.Enqueue(next);
                }
            }
        }

        var unreached = ranks.Count == 0 ? 0 : ranks.Values.Max() + 1;
        return states.ToDictionary(state => state, state => ranks.GetValueOrDefault(state, unreached), StringComparer.Ordinal);
    }

    private static List<StateNode> Place(List<string> states, Dictionary<string, int> ranks, string? initial, bool any)
    {
        var rows = new Dictionary<int, int>();
        var nodes = new List<StateNode>();
        foreach (var state in states.OrderBy(state => ranks[state]))
        {
            nodes.Add(Node(state, ranks[state], rows, initial == state, any: false));
        }

        if (any)
        {
            nodes.Add(Node(AnyState, 0, rows, initial: false, any: true));
        }

        return nodes;
    }

    private static StateNode Node(string state, int rank, Dictionary<int, int> rows, bool initial, bool any)
    {
        var row = rows.GetValueOrDefault(rank);
        rows[rank] = row + 1;
        return new StateNode(state, rank * ColumnStep, row * RowStep, initial, any);
    }
}
