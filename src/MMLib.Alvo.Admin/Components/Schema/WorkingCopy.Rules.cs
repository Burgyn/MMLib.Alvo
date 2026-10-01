using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* An entity's rules: one CEL source per operation. */
internal sealed partial class WorkingCopy
{
    /// <summary>Sets or clears one operation's rule on an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The operation — one of list, get, create, update, delete.</param>
    /// <param name="cel">The CEL source, or empty to remove the rule.</param>
    public void SetRule(string entity, string operation, string cel) => Edit(root =>
    {
        if (root["entities"]?[entity] is not JsonObject declared)
        {
            return false;
        }

        if (cel.Length == 0)
        {
            var existing = declared["rules"] as JsonObject;
            existing?.Remove(operation);
            if (existing is { Count: 0 })
            {
                declared.Remove("rules");
            }

            return true;
        }

        Ensure(declared, "rules")[operation] = cel;
        return true;
    });
}
