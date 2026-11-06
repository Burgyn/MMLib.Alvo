using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Descriptor;

using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The States tab: an entity's <c>stateMachine</c> as a graph and its <c>timers</c> as rows.</summary>
public partial class StatesTab
{
    private const double Margin = 24;

    private StateGraph? _graph;

    /// <summary>Gets or sets the entity's name.</summary>
    [Parameter, EditorRequired]
    public string Entity { get; set; } = string.Empty;

    /// <summary>Gets or sets the entity as the working copy declares it, or <see langword="null"/> when it reads as neither member.</summary>
    [Parameter]
    public EntityDescriptor? Model { get; set; }

    private string ArrowId => $"sm-arrow-{Entity}";

    /// <inheritdoc/>
    protected override void OnParametersSet() =>
        _graph = Model?.StateMachine is { } machine ? StateGraphLayout.For(machine, ValuesOf(Model, machine.Field)) : null;

    private static IReadOnlyList<string> ValuesOf(EntityDescriptor model, string field) =>
        model.Fields.TryGetValue(field, out var declared) && declared.Values is { } values ? values : [];

    private static string ViewBox(StateGraph graph) => $"{N(-Margin)} {N(-Margin)} {N(graph.Width + 2 * Margin)} {N(graph.Height + 2 * Margin)}";

    private static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string EdgePath(StateGraph graph, StateEdge edge)
    {
        var (from, to) = (NodeOf(graph, edge.From), NodeOf(graph, edge.To));
        var (startX, startY) = (from.X + StateGraphLayout.NodeWidth, from.Y + StateGraphLayout.NodeHeight / 2);
        var (endX, endY) = (to.X, to.Y + StateGraphLayout.NodeHeight / 2);
        if (to.X > from.X)
        {
            var bend = (startX + endX) / 2;
            return $"M {N(startX)} {N(startY)} C {N(bend)} {N(startY)}, {N(bend)} {N(endY)}, {N(endX)} {N(endY)}";
        }

        var below = Math.Max(from.Y, to.Y) + StateGraphLayout.NodeHeight + 18;
        return $"M {N(from.X + StateGraphLayout.NodeWidth / 2)} {N(from.Y + StateGraphLayout.NodeHeight)} "
            + $"C {N(from.X + StateGraphLayout.NodeWidth / 2)} {N(below)}, {N(to.X + StateGraphLayout.NodeWidth / 2)} {N(below)}, "
            + $"{N(to.X + StateGraphLayout.NodeWidth / 2)} {N(to.Y + StateGraphLayout.NodeHeight)}";
    }

    private static (double X, double Y) LabelAt(StateGraph graph, StateEdge edge)
    {
        var (from, to) = (NodeOf(graph, edge.From), NodeOf(graph, edge.To));
        return to.X > from.X
            ? (((from.X + StateGraphLayout.NodeWidth) + to.X) / 2, ((from.Y + to.Y) / 2) + (StateGraphLayout.NodeHeight / 2) - 6)
            : ((from.X + to.X + StateGraphLayout.NodeWidth) / 2, Math.Max(from.Y, to.Y) + StateGraphLayout.NodeHeight + 20);
    }

    private static StateNode NodeOf(StateGraph graph, string state) => graph.Nodes.First(node => node.State == state);

    private static string? Gate(StateTransition transition) =>
        (transition.Roles is { Count: > 0 } listed ? string.Join(", ", listed) : null, transition.When) switch
        {
            (null, null) => null,
            ({ } roles, null) => roles,
            (null, { }) => "when …",
            ({ } roles, { }) => $"{roles} · when …",
        };

    private static string EdgeWords(StateEdge edge) =>
        $"{edge.Transition}: {(edge.From == StateGraphLayout.AnyState ? "any state" : edge.From)} → {edge.To}";

    private string EventOf(string name, EntityTimer timer) => timer.Event ?? $"{Entity}.{name.Replace('-', '_')}";
}
