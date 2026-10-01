using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

public sealed class ExpressionSlotCheckTests
{
    private const string ListRule = "/entities/orders/rules/list";

    [Fact]
    public void A_valid_rule_has_no_error_at_its_slot() =>
        Check(ListRule, "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();

    [Fact]
    public void A_rule_naming_an_undeclared_role_is_refused_at_its_slot_as_apply_refuses_it()
    {
        var findings = Check(ListRule, "'amdin' in @user.roles");

        findings.Where(IsError).ShouldNotBeEmpty("compiling alone would pass this; only the validator knows the role catalog");
        findings.ShouldAllBe(f => JsonPointerPath.IsAtOrUnder(f.Path, ListRule));
    }

    [Fact]
    public void A_syntax_error_is_data_not_an_exception() =>
        Check(ListRule, "status ==").Where(IsError).ShouldNotBeEmpty();

    [Fact]
    public void A_good_slot_is_green_while_another_slot_is_broken()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["orders"]!["rules"]!["get"] = "status ==";

        Check(descriptor, ListRule, "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/entities/nope/rules/list")]
    [InlineData("/entities/orders/hooks/beforeCreate/7/condition")]
    [InlineData("/entities/orders/rules/list/deeper")]
    public void A_slot_the_descriptor_does_not_contain_is_refused(string jsonPointer) =>
        Should.Throw<ManagementRequestException>(() => Check(jsonPointer, "true"));

    [Fact]
    public void A_descriptor_that_is_not_json_is_refused() =>
        Should.Throw<ManagementRequestException>(() => ExpressionSlotCheck.Check(Validator(), "{not json", ListRule, "true"));

    [Fact]
    public void A_candidate_with_characters_json_escapes_reaches_the_validator_unchanged()
    {
        var findings = Check(ListRule, "'a\"b\\c' == 'x\ny'");

        findings.Where(IsError).ShouldNotBeEmpty();
        findings.ShouldAllBe(f => !f.Message.Contains("\\u", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_mutate_value_is_spliced_in_the_dollar_cel_form_the_descriptor_documents()
    {
        var descriptor = DescriptorWithMutateHook();
        const string pointer = "/entities/orders/hooks/beforeCreate/0/action/mutate/status";

        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, "'open'")
            .Where(IsError).ShouldBeEmpty();
        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, "nope(")
            .Where(IsError).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_pointer_with_escaped_segments_addresses_the_right_node()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["a/b"] = descriptor["entities"]!["orders"]!.DeepClone();

        Check(descriptor, "/entities/a~1b/rules/list", "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();
    }

    private static bool IsError(DescriptorValidationError f) => f.Severity == DescriptorValidationSeverity.Error;

    private static IReadOnlyList<DescriptorValidationError> Check(string pointer, string source) =>
        Check(Descriptor(), pointer, source);

    private static IReadOnlyList<DescriptorValidationError> Check(JsonNode descriptor, string pointer, string source) =>
        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, source);

    private static DescriptorValidator Validator() => new DescriptorValidator();

    private static JsonObject DescriptorWithMutateHook()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["orders"]!["hooks"] = new JsonObject
        {
            ["beforeCreate"] = new JsonArray(new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["mutate"] = new JsonObject { ["status"] = new JsonObject { ["$cel"] = "'open'" } },
                },
            }),
        };

        return descriptor;
    }

    private static JsonObject Descriptor() => new()
    {
        ["apiVersion"] = "alvo.dev/v1",
        ["name"] = "demo",
        ["auth"] = new JsonObject { ["roles"] = new JsonArray("clerk") },
        ["entities"] = new JsonObject
        {
            ["orders"] = new JsonObject
            {
                ["fields"] = new JsonObject
                {
                    ["status"] = new JsonObject { ["type"] = "enum", ["values"] = new JsonArray("open", "closed") },
                    ["total"] = new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 },
                    ["unit_price"] = new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 },
                },
                ["rules"] = new JsonObject { ["list"] = "true", ["get"] = "true" },
            },
        },
    };
}
