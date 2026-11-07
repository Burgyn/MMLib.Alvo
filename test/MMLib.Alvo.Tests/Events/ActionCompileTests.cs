using Microsoft.Extensions.Options;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Functions;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;

using System.Text.Json;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// A <c>function</c> and an <c>entity.update</c> action compile against the host at apply: a function's name and input
/// against what <c>AddFunction</c> registered, an update's target, payload and record id against the target entity.
/// </summary>
public sealed class ActionCompileTests
{
    private const string HookPath = "/entities/orders/hooks/afterCreate/0/action";

    [Fact]
    public void An_input_typo_is_refused_with_the_closest_property_as_the_fix()
    {
        var error = SingleError(Function("""{ "orderId": "x", "amout": 3 }"""));

        error.Path.ShouldBe($"{HookPath}/input/amout");
        error.Message.ShouldBe("'amout' is not an input of function 'issue-invoice'.");
        error.FixSuggestion.ShouldBe("Did you mean 'amount'?");
    }

    [Fact]
    public void An_unregistered_function_name_gets_a_suggestion()
    {
        var error = SingleError(Function("""{ "orderId": "x" }""", name: "issue-invoce"));

        error.Path.ShouldBe($"{HookPath}/name");
        error.Message.ShouldBe("'issue-invoce' is not a function this host registers.");
        error.FixSuggestion.ShouldBe("Did you mean 'issue-invoice'?");
    }

    [Fact]
    public void A_name_both_registered_and_declared_under_functions_is_refused()
    {
        var errors = Errors(Function("""{ "orderId": "x" }""", functionsBlock: """, "functions": { "issue-invoice": { "script": "issue.csx" } }"""));

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/functions/issue-invoice");
        error.Message.ShouldBe("'issue-invoice' is both a host function and a 'functions' entry, so a 'function' action naming it would be ambiguous.");
        error.FixSuggestion.ShouldBe("Rename the 'functions' entry, or remove it.");
    }

    [Fact]
    public void A_function_declared_only_under_functions_is_refused_as_a_script_this_build_does_not_run()
    {
        var error = SingleError(Function("""{ "orderId": "x" }""", name: "send-pdf", functionsBlock: """, "functions": { "send-pdf": { "script": "send.csx" } }"""));

        error.Path.ShouldBe($"{HookPath}/name");
        error.FixSuggestion.ShouldBe("'send-pdf' is declared under 'functions', a csx script this build does not run; register a host function instead.");
    }

    [Fact]
    public void A_missing_required_input_is_refused()
    {
        var error = SingleError(Function("""{ "amount": 3 }"""));

        error.Path.ShouldBe($"{HookPath}/input");
        error.Message.ShouldBe("Function 'issue-invoice' requires 'orderId' in its input.");
        error.FixSuggestion.ShouldBe("Add 'orderId' to 'input'.");
    }

    [Fact]
    public void A_literal_of_the_wrong_json_type_is_refused()
    {
        var error = SingleError(Function("""{ "orderId": "x", "amount": true }"""));

        error.Path.ShouldBe($"{HookPath}/input/amount");
        error.Message.ShouldBe("'amount' is boolean, and function 'issue-invoice' takes number there.");
        error.FixSuggestion.ShouldBe("Write a number value, or a {\"$cel\": \"…\"} that yields one.");
    }

    [Fact]
    public void A_cel_input_value_is_compiled_against_the_triggering_row()
    {
        Errors(Function("""{ "orderId": { "$cel": "string(new.id)" }, "amount": { "$cel": "new.total" } }""")).ShouldBeEmpty();
    }

    [Fact]
    public void A_cel_input_value_reading_old_on_create_is_refused()
    {
        var errors = Errors(Function("""{ "orderId": "x", "amount": { "$cel": "old.total" } }"""));

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(error => error.Path == $"{HookPath}/input/amount");
        errors[0].Message.ShouldContain("old.total");
    }

    [Fact]
    public void A_cel_input_value_reading_an_image_the_event_lacks_is_refused_as_a_value_not_a_condition()
    {
        var errors = Errors(Function("""{ "orderId": "x", "amount": { "$cel": "old.total" } }"""));

        errors[0].Message.ShouldStartWith("This 'afterCreate' value reads 'old.total'");
    }

    [Fact]
    public void A_function_input_with_neither_form_set_is_refused_at_apply()
    {
        var written = JsonSerializer.Serialize(new FunctionInput());

        var errors = Errors(Function(written));

        errors.ShouldNotBeEmpty();
        errors.Select(error => error.Path).ShouldBe([$"#{HookPath}"]);
    }

    [Fact]
    public void A_string_input_is_refused_as_jsonata()
    {
        var error = SingleError(Function("\"$merge([new, {'x': 1}])\""));

        error.Path.ShouldBe($"{HookPath}/input");
        error.Message.ShouldBe(UnhonouredFeatures.RawJsonata.Consequence);
        error.FixSuggestion!.ShouldEndWith("Write the input as an object of literals and {\"$cel\": \"…\"} values instead.");
    }

    [Fact]
    public void A_catalog_built_without_the_host_functions_refuses_a_function_action()
    {
        var errors = new DescriptorValidator().Validate(Function("""{ "orderId": "x" }""")).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error);

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"{HookPath}/name");
        error.Message.ShouldBe("'issue-invoice' is not a function this host registers.");
    }

    [Fact]
    public void An_entity_update_to_an_unknown_entity_gets_a_suggestion()
    {
        var error = SingleError(Update("invoise", """{ "note": "x" }"""));

        error.Path.ShouldBe($"{HookPath}/entity");
        error.FixSuggestion!.ShouldStartWith("Did you mean 'invoices'?");
    }

    [Fact]
    public void An_entity_update_of_a_computed_field_is_refused()
    {
        var error = SingleError(Update("invoices", """{ "gross": 3 }""", recordId: "\"{{new.invoice_id}}\""));

        error.Path.ShouldBe($"{HookPath}/payload/gross");
        error.Message.ShouldContain("computed");
    }

    [Fact]
    public void An_entity_update_payload_null_entry_is_refused_for_a_required_field_and_accepted_for_an_optional_one()
    {
        var required = SingleError(Update("invoices", """{ "number": null, "amount": 1 }"""));
        required.Path.ShouldBe($"{HookPath}/payload/number");
        required.Message.ShouldBe("Field 'number' is required on 'invoices', so an 'entity.update' cannot set it to null.");

        Errors(Update("invoices", """{ "number": "INV-1", "amount": 1, "note": null }""")).ShouldBeEmpty();
        Errors(Update("invoices", """{ "note": null }""", recordId: "\"{{new.invoice_id}}\"")).ShouldBeEmpty();
        SingleError(Update("invoices", """{ "number": null }""", recordId: "\"{{new.invoice_id}}\"")).Path
            .ShouldBe($"{HookPath}/payload/number", "a null into a required field fails every update, so it is refused at apply too");
    }

    [Fact]
    public void An_entity_update_record_id_is_a_uuid_or_one_placeholder_over_the_triggering_row()
    {
        Errors(Update("invoices", """{ "note": "x" }""", recordId: "\"3f2c1d4e-0000-4000-8000-000000000001\"")).ShouldBeEmpty();
        SingleError(Update("invoices", """{ "note": "x" }""", recordId: "\"not-an-id\"")).Path.ShouldBe($"{HookPath}/recordId");
        SingleError(Update("invoices", """{ "note": "x" }""", recordId: "\"{{new.invoice_id}}-x\"")).Path.ShouldBe($"{HookPath}/recordId");
        SingleError(Update("invoices", """{ "note": "x" }""", recordId: "\"{{new.missing}}\"")).Path.ShouldBe($"{HookPath}/recordId");
    }

    [Fact]
    public void An_entity_update_into_a_tenant_scoped_entity_from_an_untenanted_trigger_is_refused()
    {
        var error = SingleError(Update("ledger", """{ "note": "x" }""", tenancy: true));

        error.Path.ShouldBe($"{HookPath}/entity");
        error.Message.ShouldContain("tenant");
    }

    [Fact]
    public void An_entity_update_from_a_tenant_scoped_trigger_into_a_global_entity_is_refused()
    {
        var error = SingleError(Update("invoices", """{ "number": "INV-1", "amount": 1 }""", tenancy: true, ordersTenancy: "scoped"));

        error.Path.ShouldBe($"{HookPath}/entity");
        error.Message.ShouldContain("runs as the system in the event's tenant, under the target's rules");
    }

    [Fact]
    public void An_entity_update_payload_template_string_is_refused_with_the_cel_form_as_the_fix()
    {
        var error = SingleError(Update("invoices", """{ "number": "INV-{{new.total}}", "amount": 1 }"""));

        error.Path.ShouldBe($"{HookPath}/payload/number");
        error.Message.ShouldContain("written verbatim");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("$cel");
        Errors(Update("invoices", """{ "number": "INV {not one}", "amount": 1 }""")).ShouldBeEmpty();
    }

    [Fact]
    public void An_entity_update_record_id_placeholder_must_read_a_uuid_field()
    {
        var error = SingleError(Update("invoices", """{ "note": "x" }""", recordId: "\"{{new.total}}\""));

        error.Path.ShouldBe($"{HookPath}/recordId");
        error.Message.ShouldBe("'total' is not a UUID field, so it cannot name the record to update.");
        Errors(Update("invoices", """{ "note": "x" }""", recordId: "\"{{new.id}}\"")).ShouldBeEmpty();
    }

    [Fact]
    public void A_host_less_validator_refuses_entity_update()
    {
        var errors = new DescriptorValidator().Validate(Update("invoices", """{ "number": "INV-1", "amount": 1 }""")).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error);

        errors.ShouldHaveSingleItem().Path.ShouldBe($"{HookPath}/type");
    }

    [Fact]
    public void An_entity_update_without_a_ledger_is_refused()
    {
        var errors = Validator(HostWith(ledger: false)).Validate(Update("invoices", """{ "number": "INV-1", "amount": 1 }""")).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error);

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"{HookPath}/type");
        error.Message.ShouldBe("This composition keeps no command ledger, so 'entity.update' cannot be written exactly once.");
        error.FixSuggestion.ShouldBe("Use a relational provider (UseSqlite, UsePostgreSql).");
    }

    [Fact]
    public void An_entity_update_literal_outside_the_enum_values_is_refused_at_apply()
    {
        var error = SingleError(Update("invoices", """{ "number": "INV-1", "kind": "bogus" }"""));

        error.Path.ShouldBe($"{HookPath}/payload/kind");
        error.Message.ShouldStartWith("This literal breaks the 'enum-value' facet 'kind' declares");
    }

    [Fact]
    public void An_entity_update_literal_past_max_length_is_refused_at_apply()
    {
        var error = SingleError(Update("invoices", $$"""{ "number": "{{new string('9', 21)}}" }"""));

        error.Path.ShouldBe($"{HookPath}/payload/number");
        error.Message.ShouldContain("'max-length' facet 'number' declares");
    }

    [Fact]
    public void An_entity_update_create_missing_a_required_field_is_refused()
    {
        var error = SingleError(Update("invoices", """{ "amount": 1 }"""));

        error.Path.ShouldBe($"{HookPath}/payload");
        error.Message.ShouldContain("leaves out 'number', which is required");
    }

    [Fact]
    public void Http_call_is_still_refused_by_name()
    {
        var error = SingleError(Descriptor("""{ "type": "http.call", "url": "https://example.com/hook" }"""));

        error.Path.ShouldBe($"{HookPath}/type");
        error.Message.ShouldBe(UnhonouredFeatures.UnhonouredAction("http.call").Consequence);
    }

    private static DescriptorValidationError SingleError(string descriptor) => Errors(descriptor).ShouldHaveSingleItem();

    private static IReadOnlyList<DescriptorValidationError> Errors(string descriptor) =>
        [.. Validator(HostWith(ledger: true)).Validate(descriptor).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private static DescriptorValidator Validator(DescriptorHost host) =>
        new(CelFixtures.Compiler, Options.Create(new AlvoOptions()), host);

    private static DescriptorHost HostWith(bool ledger)
    {
        var function = new FunctionRegistration(
            "issue-invoice", 0, typeof(IssueInvoice), typeof(InvoiceInput), FunctionInputSchema.For(typeof(InvoiceInput)));

        return new DescriptorHost(
            new Dictionary<string, FunctionRegistration>(StringComparer.Ordinal) { [function.Id] = function },
            HasTenantCatalog: false,
            HasActionLedger: ledger);
    }

    private static string Function(string input, string name = "issue-invoice", string functionsBlock = "") =>
        Descriptor($$"""{ "type": "function", "name": "{{name}}", "input": {{input}} }""", functionsBlock);

    private static string Update(string entity, string payload, string? recordId = null, bool tenancy = false, string ordersTenancy = "global") =>
        Descriptor(
            $$"""{ "type": "entity.update", "entity": "{{entity}}"{{(recordId is null ? string.Empty : $", \"recordId\": {recordId}")}}, "payload": {{payload}} }""",
            tenancy ? """, "tenancy": { "enabled": true }""" : string.Empty,
            ordersTenancy);

    private static string Descriptor(string action, string extra = "", string ordersTenancy = "global") => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "actions"{{extra}},
          "entities": {
            "orders": {
              "tenancy": "{{ordersTenancy}}",
              "fields": {
                "total": { "type": "decimal", "precision": 12, "scale": 2 },
                "invoice_id": { "type": "uuid" }
              },
              "hooks": {
                "afterCreate": [ { "action": {{action}} } ]
              }
            },
            "invoices": {
              "tenancy": "global",
              "fields": {
                "number": { "type": "string", "required": true, "maxLength": 20 },
                "amount": { "type": "decimal", "precision": 12, "scale": 2 },
                "gross": { "type": "decimal", "precision": 12, "scale": 2, "computed": "amount + amount" },
                "note": { "type": "string", "maxLength": 200 },
                "kind": { "type": "enum", "values": ["proforma", "final"] }
              }
            },
            "ledger": {
              "fields": {
                "note": { "type": "string", "maxLength": 200 }
              }
            }
          }
        }
        """;

    internal sealed record InvoiceInput(string OrderId, decimal Amount = 0, string? Note = null);

    private sealed class IssueInvoice : IAlvoFunction<InvoiceInput>
    {
        public Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
