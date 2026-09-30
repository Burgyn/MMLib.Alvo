using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>Hand-built turns over a small descriptor with the members the cases touch.</summary>
internal static class Turns
{
    internal const string Original = """
        {
          "name": "bike-workshop",
          "entities": {
            "customers": { "fields": {
              "first_name": { "type": "string", "required": true, "maxLength": 60 },
              "last_name": { "type": "string", "required": true, "maxLength": 60 },
              "phone": { "type": "string", "required": true },
              "street": { "type": "string", "maxLength": 120 } } },
            "bikes": { "fields": { "brand": { "type": "string" } } },
            "parts": { "fields": { "name": { "type": "string" }, "unit_price": { "type": "decimal", "required": true, "precision": 8, "scale": 2 } },
              "rules": { "delete": "'admin' in @user.roles || 'manager' in @user.roles" } },
            "technicians": { "fields": { "full_name": { "type": "string" } } },
            "rentals": { "fields": {
              "customer_id": { "type": "ref", "entity": "customers", "required": true },
              "status": { "type": "enum", "required": true, "values": ["reserved", "active", "returned", "overdue", "cancelled"] },
              "returned_at": { "type": "datetime" } },
              "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "rental-desk" } } ] } },
            "service_orders": { "fields": {
              "status": { "type": "enum", "required": true, "values": ["received", "ready", "collected"] },
              "assigned_user_id": { "type": "uuid" } },
              "rules": { "list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles" },
              "hooks": { "afterUpdate": [] } },
            "order_lines": { "fields": {
              "order_id": { "type": "ref", "entity": "service_orders", "required": true },
              "part_id": { "type": "ref", "entity": "parts" } },
              "indexes": [ { "fields": ["order_id"] } ] }
          }
        }
        """;

    /// <summary>The original descriptor after <paramref name="edit"/>, as a proposal would carry it.</summary>
    internal static string Edited(Action<JsonObject> edit)
    {
        var document = JsonNode.Parse(Original)!.AsObject();
        edit(document);
        return document.ToJsonString();
    }

    internal static JsonObject Fields(this JsonObject document, string entity) =>
        document["entities"]![entity]!["fields"]!.AsObject();

    /// <summary>A turn: the reply, the dry-run calls with their answers, and the proposal the turn filed.</summary>
    internal static TurnRecord Turn(
        string? proposed = null, string answer = "", IReadOnlyList<string>? refusals = null, params RecordedCall[] calls)
    {
        var updates = new List<AssistantUpdate>();
        updates.AddRange(calls.Select(call => new AssistantUpdate.ToolInvoked(call.Tool)));
        updates.Add(new AssistantUpdate.Text(answer));
        if (proposed is not null)
        {
            updates.Add(new AssistantUpdate.Proposal(proposed, 1, "summary", refusals ?? []));
        }

        return new TurnRecord(Original, updates, TimeSpan.FromSeconds(1), calls.Length + 1, calls.Length, 100, calls);
    }

    /// <summary>A <c>propose_change</c> call that answered <paramref name="outcome"/>.</summary>
    internal static RecordedCall Propose(JsonObject outcome, int round = 1) =>
        new(round, $"call_{round}", "propose_change", Arguments: null, outcome.ToJsonString());

    /// <summary>A <c>load_skill</c> call, answered with the skill's body as the provider wraps it.</summary>
    internal static RecordedCall Loads(string skill, int round = 1) =>
        new(round, $"load_{skill}_{round}", "load_skill", new Dictionary<string, object?> { ["skillName"] = skill }, "<instructions>…</instructions>");

    /// <summary>A tool call that is not a dry run.</summary>
    internal static RecordedCall Read(string tool, int round = 1) => new(round, $"call_{round}", tool, Arguments: null, "{}");

    internal static JsonObject Valid() => new() { ["valid"] = true, ["violations"] = new JsonArray() };

    internal static JsonObject Refused(
        string source, string message, string? fix = null, string? code = null, bool destructive = false) => new()
        {
            ["valid"] = false,
            ["plan"] = new JsonObject { ["isEmpty"] = false, ["hasDestructiveChanges"] = destructive, ["steps"] = new JsonArray() },
            ["violations"] = new JsonArray(new JsonObject
            {
                ["source"] = source,
                ["pointer"] = "",
                ["message"] = message,
                ["fix"] = fix,
                ["code"] = code,
            }),
        };
}
