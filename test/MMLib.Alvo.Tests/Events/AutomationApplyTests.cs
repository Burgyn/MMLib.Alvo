using Microsoft.Extensions.Options;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Functions;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The <c>automation</c> block compiles at apply: an entity event or a five-field UTC cron as the trigger, the condition at
/// the trigger's point, every action through the one action compiler — and what this build does not run is refused by name.
/// </summary>
public sealed class AutomationApplyTests
{
    private const string Webhook = """{ "type": "webhook", "endpoint": "erp" }""";

    [Theory]
    [InlineData("""{ "trigger": { "event": "entity.orders.created" }, "delivery": "batch", "actions": [WEBHOOK] }""", "/automation/r/delivery", "'batch'")]
    [InlineData("""{ "trigger": { "event": "entity.orders.created" }, "actions": [{ "type": "http.call", "url": "https://x.example/h" }] }""", "/automation/r/actions/0/type", "'http.call'")]
    [InlineData("""{ "trigger": { "event": "entity.orders.*" }, "actions": [WEBHOOK] }""", "/automation/r/trigger/event", "wildcard")]
    public void Batch_delivery_http_call_and_a_wildcard_are_refused_by_name(string rule, string path, string named)
    {
        var error = SingleError(Rule(rule.Replace("WEBHOOK", Webhook, StringComparison.Ordinal)));

        error.Path.ShouldBe(path);
        error.Message.ShouldContain(named);
    }

    [Fact]
    public void A_batch_event_pattern_is_refused_as_batch()
    {
        var error = SingleError(Rule(EventRule("entity.orders.created.batch")));

        error.Path.ShouldBe("/automation/r/trigger/event");
        error.Message.ShouldContain("'batch'");
        error.FixSuggestion.ShouldBe(UnhonouredFeatures.BatchDelivery.Fix);
    }

    [Theory]
    [InlineData("auth.users.created")]
    [InlineData("storage.files.created")]
    public void An_auth_or_storage_trigger_is_refused_as_trigger_source(string pattern)
    {
        var error = SingleError(Rule(EventRule(pattern)));

        error.Path.ShouldBe("/automation/r/trigger/event");
        error.Message.ShouldContain("'trigger.source'");
        error.FixSuggestion.ShouldBe(UnhonouredFeatures.NonEntityTrigger.Fix);
    }

    [Fact]
    public void An_unknown_trigger_entity_gets_a_suggestion()
    {
        var error = SingleError(Rule(EventRule("entity.order.created")));

        error.Path.ShouldBe("/automation/r/trigger/event");
        error.Message.ShouldBe("'order' is not an entity of this descriptor, so this rule would never fire.");
        error.FixSuggestion.ShouldNotBeNull().ShouldStartWith("Did you mean 'orders'?");
    }

    [Fact]
    public void An_unknown_operation_gets_a_suggestion()
    {
        var error = SingleError(Rule(EventRule("entity.orders.update")));

        error.Path.ShouldBe("/automation/r/trigger/event");
        error.Message.ShouldBe("'update' is not an operation an entity emits, so this rule would never fire.");
        error.FixSuggestion.ShouldBe("Did you mean 'updated'? An entity emits created, updated and deleted.");
    }

    [Fact]
    public void A_malformed_cron_is_refused_with_its_reason()
    {
        var error = SingleError(Rule(CronRule("61 * * * *")));

        error.Path.ShouldBe("/automation/r/trigger/schedule");
        error.Message.ShouldContain("five-field cron");
    }

    [Fact]
    public void A_condition_on_a_cron_rule_is_refused()
    {
        var error = SingleError(Rule("""{ "trigger": { "schedule": "0 2 * * *" }, "condition": "true", "actions": [ { "type": "function", "name": "close-day" } ] }"""));

        error.Path.ShouldBe("/automation/r/condition");
    }

    [Fact]
    public void A_tenanted_cron_rule_without_a_tenant_catalog_is_refused()
    {
        var errors = Errors(Rule(CronRule("0 2 * * *"), tenanted: true), Host(tenantCatalog: false));

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/automation/r/trigger/schedule");
        error.Message.ShouldBe("A schedule rule on a tenanted project runs once per tenant, and this host registers no IAlvoTenantCatalog.");
        error.FixSuggestion.ShouldBe("Register an IAlvoTenantCatalog, or trigger the rule from an entity event.");
        Errors(Rule(CronRule("0 2 * * *"), tenanted: true), Host(tenantCatalog: true)).ShouldBeEmpty();
    }

    [Fact]
    public void A_disabled_rule_is_validated_and_not_compiled()
    {
        SingleError(Rule("""{ "enabled": false, "trigger": { "event": "entity.order.created" }, "actions": [WEBHOOK] }""".Replace("WEBHOOK", Webhook, StringComparison.Ordinal)))
            .Path.ShouldBe("/automation/r/trigger/event");

        Catalog(Rule("""{ "enabled": false, "trigger": { "event": "entity.orders.created" }, "actions": [WEBHOOK] }""".Replace("WEBHOOK", Webhook, StringComparison.Ordinal)))
            .Automation.ShouldBeEmpty();
    }

    [Fact]
    public void Rule_names_differing_only_by_a_dash_and_an_underscore_are_refused()
    {
        var json = Descriptor($$"""
            "close-day": {{CronRule("0 2 * * *")}},
            "close_day": {{CronRule("0 3 * * *")}}
            """);

        var error = SingleError(json);

        error.Path.ShouldBe("/automation/close_day");
        error.Message.ShouldContain("'close-day'");
    }

    [Fact]
    public void An_event_rule_compiles_its_trigger_its_condition_and_its_actions()
    {
        var rule = Catalog(Rule("""{ "trigger": { "event": "entity.orders.updated" }, "condition": "changed(total) && new.total > 10.0", "actions": [WEBHOOK, { "type": "function", "name": "close-day" }] }""".Replace("WEBHOOK", Webhook, StringComparison.Ordinal)))
            .Automation.ShouldHaveSingleItem();

        rule.Name.ShouldBe("r");
        rule.Trigger.ShouldBe(new EventTrigger("orders", HandlerVerb.Updated, "afterUpdate"));
        rule.Condition.ShouldNotBeNull();
        rule.Actions.Count.ShouldBe(2);
        rule.Actions.ShouldAllBe(action => action.SubscriberId.StartsWith("/automation/r/actions#", StringComparison.Ordinal));
        rule.Actions.Select(action => action.SubscriberId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
    }

    [Fact]
    public void An_event_rule_condition_reading_old_on_a_create_is_refused()
    {
        SingleError(Rule("""{ "trigger": { "event": "entity.orders.created" }, "condition": "old.total > 1.0", "actions": [WEBHOOK] }""".Replace("WEBHOOK", Webhook, StringComparison.Ordinal)))
            .Path.ShouldBe("/automation/r/condition");
    }

    [Fact]
    public void A_cron_rule_compiles_a_trigger_only_schedule_and_its_actions()
    {
        var rule = Catalog(Rule(CronRule("0 2 * * *"))).Automation.ShouldHaveSingleItem();

        var cron = rule.Trigger.ShouldBeOfType<CronTrigger>();
        cron.Cron.ShouldBe("0 2 * * *");
        cron.SubscriberId.ShouldStartWith("/automation/r#");
        cron.EventType.ShouldBe("alvo.automation.r");
        rule.Actions.ShouldHaveSingleItem().SubscriberId.ShouldStartWith("/automation/r/actions#");
    }

    [Fact]
    public void A_cron_rule_event_type_reads_a_dash_as_an_underscore()
    {
        var json = Descriptor($$""" "nightly-close": {{CronRule("0 2 * * *")}} """);

        Catalog(json).Automation.ShouldHaveSingleItem().Trigger.ShouldBeOfType<CronTrigger>().EventType.ShouldBe("alvo.automation.nightly_close");
    }

    [Fact]
    public void A_cron_rule_action_reads_no_row()
    {
        var errors = Errors(Rule("""{ "trigger": { "schedule": "0 2 * * *" }, "actions": [ { "type": "function", "name": "close-day", "input": { "note": { "$cel": "new.note" } } } ] }"""), Host());

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(error => error.Path == "/automation/r/actions/0/input/note");
    }

    [Fact]
    public void An_automation_descriptor_is_no_longer_warned_and_automation_is_honoured()
    {
        var descriptor = AlvoDescriptor.Parse(Rule(CronRule("0 2 * * *")));

        UnhonouredSubsystems.DeclaredBy(descriptor).Select(subsystem => subsystem.Block).ShouldNotContain("automation");
        CapabilityReport.Honoured.ShouldContain("automation");
    }

    private static string EventRule(string pattern) =>
        $$"""{ "trigger": { "event": "{{pattern}}" }, "actions": [{{Webhook}}] }""";

    private static string CronRule(string cron) =>
        $$"""{ "trigger": { "schedule": "{{cron}}" }, "actions": [ { "type": "function", "name": "close-day" } ] }""";

    private static DescriptorValidationError SingleError(string descriptor) => Errors(descriptor, Host()).ShouldHaveSingleItem();

    private static IReadOnlyList<DescriptorValidationError> Errors(string descriptor, DescriptorHost host) =>
        [.. new DescriptorValidator(CelFixtures.Compiler, Options.Create(new AlvoOptions()), host).Validate(descriptor).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    private static PolicyCatalog Catalog(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        var schema = DescriptorToSchemaMapper.Map(descriptor);

        return PolicyCatalog.Build(descriptor, schema, CelFixtures.Compiler, Host());
    }

    private static DescriptorHost Host(bool tenantCatalog = false)
    {
        var function = new FunctionRegistration(
            "close-day", 0, typeof(CloseDay), typeof(CloseInput), FunctionInputSchema.For(typeof(CloseInput)));

        return new DescriptorHost(
            new Dictionary<string, FunctionRegistration>(StringComparer.Ordinal) { [function.Id] = function },
            HasTenantCatalog: tenantCatalog,
            HasActionLedger: true);
    }

    private static string Rule(string rule, bool tenanted = false) => Descriptor($"\"r\": {rule}", tenanted);

    private static string Descriptor(string automation, bool tenanted = false) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "automations"{{(tenanted ? ", \"tenancy\": { \"enabled\": true }" : string.Empty)}},
          "webhooks": { "endpoints": { "erp": { "url": "https://erp.example/hook", "secretRef": "erp-signing" } } },
          "entities": {
            "orders": {
              "tenancy": "{{(tenanted ? "scoped" : "global")}}",
              "fields": {
                "total": { "type": "decimal", "precision": 12, "scale": 2 },
                "note": { "type": "string", "maxLength": 200 }
              }
            }
          },
          "automation": {
            {{automation}}
          }
        }
        """;

    internal sealed record CloseInput(string? Note = null);

    private sealed class CloseDay : IAlvoFunction<CloseInput>
    {
        public Task InvokeAsync(CloseInput input, AlvoFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
