using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The function action: a host function registered with AddFunction, and a form for its input. */
internal sealed partial class HookBuilder
{
    /// <summary>The kind that calls a host function.</summary>
    public const string Function = "function";

    /// <summary>The host function the action calls, by the name it was registered under.</summary>
    public string FunctionName { get; set; } = string.Empty;

    /// <summary>The function's input properties, one row each.</summary>
    public List<FunctionInputRow> InputRows { get; } = [];

    /// <summary>Switches to <paramref name="name"/>: its rows replace the old ones, keeping what was typed under the same name.</summary>
    /// <param name="name">The registered function.</param>
    /// <param name="inputSchema">Its input schema from the extension catalog, or <see langword="null"/> when it exports none.</param>
    public void ChooseFunction(string name, JsonElement? inputSchema)
    {
        FunctionName = name;
        var typed = InputRows.ToDictionary(row => row.Name, StringComparer.Ordinal);
        var rows = inputSchema is { } schema ? FunctionInputRows.From(schema) : [];
        InputRows.Clear();
        InputRows.AddRange(rows.Select(row => typed.TryGetValue(row.Name, out var kept) ? kept.Describe(row) : row));
    }

    /// <summary>
    /// Describes the rows an opened hook carries by the function's schema, in place, and adds the properties it did not name —
    /// a key the schema does not declare is kept, so the apply names it rather than the editor dropping it.
    /// </summary>
    /// <param name="inputSchema">The function's input schema.</param>
    public void Describe(JsonElement inputSchema)
    {
        var described = FunctionInputRows.From(inputSchema);
        var byName = described.ToDictionary(row => row.Name, StringComparer.Ordinal);
        for (var index = 0; index < InputRows.Count; index++)
        {
            if (byName.Remove(InputRows[index].Name, out var row))
            {
                InputRows[index] = InputRows[index].Describe(row);
            }
        }

        InputRows.AddRange(described.Where(row => byName.ContainsKey(row.Name)));
    }

    private string? FunctionMissing()
    {
        if (Blank(FunctionName))
        {
            return "A function action names a function the host registered with AddFunction.";
        }

        var missing = InputRows.FirstOrDefault(row => row.Required && Blank(row.Text));
        return missing is not null ? $"'{missing.Name}' is required by {FunctionName}." : null;
    }

    private string? FunctionRefusal() => InputRows
        .Where(row => row.Mode == MutateMode.Literal && !Blank(row.Text))
        .Select(row => FunctionInputRows.TryLiteral(row, out _, out var refusal) ? null : refusal)
        .FirstOrDefault(refusal => refusal is not null);

    private JsonObject FunctionAction()
    {
        var action = new JsonObject { ["type"] = Function, ["name"] = FunctionName };
        var input = new JsonObject();
        foreach (var row in InputRows.Where(Writes))
        {
            input[row.Name] = InputValue(row);
        }

        if (input.Count > 0)
        {
            action["input"] = input;
        }

        return action;
    }

    private static bool Writes(FunctionInputRow row) => !Blank(row.Text) || row.WritesNullWhenEmpty || row.IsDeclared;

    private static JsonNode? InputValue(FunctionInputRow row)
    {
        if (row.Mode == MutateMode.Expression)
        {
            return Blank(row.Text) ? null : new JsonObject { ["$cel"] = row.Text };
        }

        if (Blank(row.Text) && !row.IsDeclared)
        {
            return null;
        }

        return FunctionInputRows.TryLiteral(row, out var value, out _) ? value : JsonValue.Create(row.Text);
    }

    private void LoadFunction(JsonObject action)
    {
        Kind = Function;
        FunctionName = (string)action["name"]!;
        InputRows.Clear();
        if (action["input"] is JsonObject input)
        {
            InputRows.AddRange(input.Select(pair => FunctionInputRow.Opened(pair.Key, pair.Value)));
        }
    }

    private static bool HoldsInput(FunctionInputRow row) => row.Text.Length > 0;
}
