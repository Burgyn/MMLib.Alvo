using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A virtual field exists only in a read's answer, after every rule and hook has run, so every CEL slot refuses it — a
/// rule, a hook condition, a mutate and a computed column alike.
/// </summary>
public sealed class CelVirtualFieldTests
{
    private const string Refusal =
        "Field 'display' is virtual: it exists only in a read's answer, after every rule and hook has run, so no expression can read it.";

    private static readonly EntitySchema _notes = new()
    {
        Name = "notes",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "display", Type = FieldType.String, Nullable = true, IsVirtual = true },
        ],
    };

    [Theory]
    [InlineData(CelProfile.Rule, "display == 'x'")]
    [InlineData(CelProfile.Condition, "new.display == 'x'")]
    [InlineData(CelProfile.Condition, "changed(display)")]
    [InlineData(CelProfile.Mutate, "new.display")]
    [InlineData(CelProfile.Computed, "display")]
    public void Every_cel_slot_refuses_a_virtual_field(CelProfile profile, string expression)
    {
        var result = CelFixtures.Compiler.Compile(expression, profile, _notes);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Message.ShouldBe(Refusal);
        error.FixSuggestion.ShouldBe("Read a stored field, or compute the value in the enricher.");
    }

    [Fact]
    public void A_rule_a_before_hook_and_a_validation_reading_a_virtual_field_are_refused_at_apply()
    {
        const string Json = """
            {"$schema": "https://alvo.dev/schema/v1/project.json", "apiVersion": "alvo.dev/v1", "name": "virtuals", "entities": {
              "notes": {
                "fields": {
                  "title": {"type": "string", "validation": "value != display"},
                  "display": {"type": "string", "virtual": true}
                },
                "rules": {"list": "display == 'x'", "get": "true", "create": "true", "update": "true", "delete": "true"},
                "hooks": {"beforeCreate": [{"condition": "new.display == 'x'", "action": {"reject": "no"}}]}
              }
            }}
            """;

        var refused = new DescriptorValidator().Validate(Json).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)
            .Where(error => error.Message.Contains(Refusal, StringComparison.Ordinal))
            .Select(error => error.Path)
            .ToList();

        refused.Count.ShouldBe(3, string.Join(", ", refused));
        refused.ShouldContain(path => path.EndsWith("/rules/list", StringComparison.Ordinal));
        refused.ShouldContain(path => path.EndsWith("/hooks/beforeCreate/0/condition", StringComparison.Ordinal));
        refused.ShouldContain(path => path.EndsWith("/fields/title/validation", StringComparison.Ordinal));
    }
}
