using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The States tab's drawing: one column per step from the initial state, one row per state in a column, one edge per
/// source of each transition, and a <c>*</c> node for a transition that leaves any state.
/// </summary>
public sealed class StateGraphLayoutTests
{
    private static readonly string[] _values = ["draft", "confirmed", "cancelled", "done", "archived"];

    private static readonly EntityStateMachine _machine = new()
    {
        Field = "status",
        Initial = "draft",
        Transitions = new Dictionary<string, StateTransition>
        {
            ["confirm"] = new() { From = ["draft"], To = "confirmed" },
            ["cancel"] = new() { From = ["draft", "confirmed"], To = "cancelled", Roles = ["manager"] },
            ["close"] = new() { From = ["confirmed"], To = "done", When = "new.starts_at < now()" },
        },
    };

    [Fact]
    public void Ranks_follow_the_transitions_from_the_initial_state()
    {
        var graph = StateGraphLayout.For(_machine, _values);

        var columns = graph.Nodes.ToDictionary(node => node.State, node => node.X);
        columns["draft"].ShouldBeLessThan(columns["confirmed"]);
        columns["cancelled"].ShouldBe(columns["confirmed"]);
        columns["done"].ShouldBeGreaterThan(columns["confirmed"]);
        columns["archived"].ShouldBeGreaterThan(columns["done"]);
        graph.Nodes.Single(node => node.State == "draft").Initial.ShouldBeTrue();
    }

    [Fact]
    public void Without_an_initial_state_the_first_value_starts_the_graph()
    {
        var graph = StateGraphLayout.For(_machine with { Initial = null }, _values);

        graph.Nodes.MinBy(node => node.X)!.State.ShouldBe("draft");
        graph.Nodes.ShouldAllBe(node => !node.Initial);
    }

    [Fact]
    public void A_wildcard_transition_starts_at_the_any_state_node()
    {
        var machine = _machine with
        {
            Transitions = new Dictionary<string, StateTransition>(_machine.Transitions)
            {
                ["reopen"] = new() { From = ["*"], To = "draft", Roles = ["admin"] },
            },
        };

        var graph = StateGraphLayout.For(machine, _values);

        graph.Nodes.Single(node => node.Any).State.ShouldBe("*");
        graph.Edges.Single(edge => edge.Transition == "reopen").ShouldBe(new StateEdge("reopen", "*", "draft", Gated: true));
    }

    [Fact]
    public void Every_transition_is_one_edge_per_source()
    {
        var edges = StateGraphLayout.For(_machine, _values).Edges;

        edges.Select(edge => (edge.Transition, edge.From, edge.To)).ShouldBe(
        [
            ("confirm", "draft", "confirmed"),
            ("cancel", "draft", "cancelled"),
            ("cancel", "confirmed", "cancelled"),
            ("close", "confirmed", "done"),
        ]);
        edges.Select(edge => edge.Gated).ShouldBe([false, true, true, true]);
    }

    [Fact]
    public void Nodes_never_overlap()
    {
        var graph = StateGraphLayout.For(_machine, _values);

        graph.Nodes.Select(node => (node.X, node.Y)).Distinct().Count().ShouldBe(graph.Nodes.Count);
        graph.Nodes.ShouldAllBe(node => node.X + StateGraphLayout.NodeWidth <= graph.Width && node.Y + StateGraphLayout.NodeHeight <= graph.Height);
    }
}
