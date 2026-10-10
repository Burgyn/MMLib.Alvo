using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

internal static class SchemaFixture
{
    internal const string Json = """
    {
      "type": "object", "required": ["name", "items"],
      "properties": {
        "name": { "type": "string", "pattern": "^[a-z]+$", "description": "The name." },
        "items": {
          "type": "object", "description": "Named items.",
          "propertyNames": { "pattern": "^[a-z_]+$" },
          "properties": { "users": false },
          "additionalProperties": { "$ref": "#/$defs/item" }
        }
      },
      "$defs": {
        "item": {
          "type": "object", "description": "One item.", "required": ["kind"],
          "properties": {
            "kind": { "enum": ["a", "b"], "description": "The kind.", "default": "a" },
            "size": { "type": "integer", "description": "Only for b." },
            "child": { "$ref": "#/$defs/item", "description": "A nested item." },
            "steps": { "type": "array", "description": "Steps.", "items": { "$ref": "#/$defs/step" } }
          },
          "allOf": [
            { "if": { "properties": { "kind": { "const": "b" } } }, "then": { "required": ["size"] } },
            { "if": { "not": { "properties": { "kind": { "const": "b" } } } }, "then": { "properties": { "size": false } } },
            { "if": { "required": ["child"] }, "then": { "properties": { "steps": false } } },
            { "if": { "properties": { "kind": { "minLength": 1 } } }, "then": { "required": ["size"] } }
          ]
        },
        "step": {
          "description": "A step.",
          "oneOf": [
            { "type": "object", "properties": { "type": { "const": "x" }, "url": { "type": "string", "description": "Target." } } },
            { "type": "object", "properties": { "type": { "const": "y" }, "url": { "type": "string" }, "body": { "type": "string", "description": "Body." } } }
          ]
        }
      }
    }
    """;

    internal static JsonObject Load() => JsonNode.Parse(Json)!.AsObject();
}
