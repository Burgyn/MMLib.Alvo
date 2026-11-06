namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// The <c>function</c> action's <c>input</c>: the object form of literals and tagged CEL values, and the
/// JSONata string form it widened from.
/// </summary>
public class FunctionActionSchemaTests
{
    [Fact]
    public void An_object_input_of_literals_and_cel_is_valid()
    {
        var failures = Failures("""{ "orderId": "{{new.id}}", "amount": { "$cel": "new.total" }, "note": null }""");

        failures.ShouldBeEmpty("the object form maps each input property to a literal or a tagged CEL value");
    }

    [Fact]
    public void A_string_input_is_still_valid()
    {
        var failures = Failures("\"{ 'id': new.id }\"");

        failures.ShouldBeEmpty("the JSONata string form stays schema-valid; widening the slot is additive");
    }

    [Fact]
    public void An_input_that_is_a_number_is_invalid()
    {
        var failures = Failures("42");

        failures.ShouldNotBeEmpty("an input is an object or a JSONata string, never a bare number");
    }

    private static IReadOnlyList<(string Pointer, string Message)> Failures(string input) =>
        SchemaValidator.Failures(SchemaValidator.Load(), $$"""
            {
              "$schema": "https://alvo.dev/schema/v1/project.json",
              "apiVersion": "alvo.dev/v1",
              "name": "function-input",
              "description": "One after-hook calling a host function.",
              "entities": {
                "orders": {
                  "description": "Orders that issue an invoice.",
                  "fields": {
                    "total": { "type": "decimal", "precision": 12, "scale": 2 }
                  },
                  "hooks": {
                    "afterCreate": [
                      { "action": { "type": "function", "name": "issue-invoice", "input": {{input}} } }
                    ]
                  }
                }
              }
            }
            """);
}
