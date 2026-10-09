using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Opening a declared hook: the editor loads what it can draw, and nothing else (spec §5.2). */
internal sealed partial class HookBuilder
{
    /// <summary>A builder holding a declared hook, its point fixed — or <see langword="null"/> when the editor cannot draw it.</summary>
    /// <param name="point">The point it is declared at.</param>
    /// <param name="hook">The declared hook; not modified.</param>
    /// <param name="fields">The entity's fields as the working copy declares them.</param>
    /// <returns>The builder, or <see langword="null"/> for a hook <see cref="HookShape"/> calls undrawable.</returns>
    public static HookBuilder? From(string point, JsonObject hook, IReadOnlyDictionary<string, FieldSchema> fields)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (HookShape.Undrawable(hook, point) is not null || hook["action"] is not JsonObject action)
        {
            return null;
        }

        var builder = new HookBuilder { Fields = fields, Condition = (string?)hook["condition"] ?? string.Empty };
        builder.Choose(point);
        builder.PointLocked = true;
        builder.Load(action);
        return builder;
    }

    private void Load(JsonObject action)
    {
        if (action.ContainsKey(Reject))
        {
            (Kind, RejectMessage) = (Reject, (string)action[Reject]!);
        }
        else if (action[Mutate] is JsonObject patch)
        {
            Kind = Mutate;
            LoadRows(patch);
        }
        else if ((string?)action["type"] == Webhook)
        {
            (Kind, Endpoint, Payload) = (Webhook, (string)action["endpoint"]!, (string?)action["payload"] ?? string.Empty);
        }
        else
        {
            (Kind, Template, To) = (Email, (string)action["template"]!, (string)action["to"]!);
        }
    }

    private void LoadRows(JsonObject patch)
    {
        MutateRows.Clear();
        foreach (var (field, value) in patch)
        {
            MutateRows.Add(value is JsonObject tagged
                ? new MutateRow(field, MutateMode.Expression, (string)tagged["$cel"]!)
                : MutateLiteral.Row(field, value));
        }
    }
}
