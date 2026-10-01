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
    public void A_source_over_the_schema_length_cap_is_refused_at_its_slot_as_apply_refuses_it()
    {
        var findings = Check(ListRule, new string('a', 2001));

        findings.Where(IsError).ShouldNotBeEmpty("apply's schema pass refuses a rule source over 2000 characters");
    }

    [Fact]
    public void A_schema_error_elsewhere_makes_a_bad_slot_not_judged_with_one_error_at_the_slot()
    {
        var findings = CheckWithSchemaErrorElsewhere(ListRule, "'amdin' in @user.roles");

        var finding = findings.Where(IsError).ShouldHaveSingleItem();
        finding.Path.ShouldBe(ListRule);
        finding.Message.ShouldContain("not judged");
        finding.Message.ShouldContain("'/title'", Case.Sensitive, "it names where the schema fails");
        finding.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_schema_error_elsewhere_makes_a_good_slot_not_judged_too()
    {
        var findings = CheckWithSchemaErrorElsewhere(ListRule, "'clerk' in @user.roles");

        findings.Where(IsError).ShouldHaveSingleItem().Message.ShouldContain("not judged");
    }

    /// <summary>
    /// Descriptors the schema accepts and the mapper refuses: the semantic pass reports them, and the rule pass never
    /// runs, so the slot is not judged just as with a schema error.
    /// </summary>
    /// <returns>The edit that breaks the descriptor elsewhere.</returns>
    public static TheoryData<string, string> MapperRefusals() => new()
    {
        { "an integer default beyond range", "zz-int" },
        { "an enum default outside its values", "zz-enum" },
        { "an audit entity declaring a managed column", "zz-audit" },
    };

    [Theory]
    [MemberData(nameof(MapperRefusals))]
    public void A_refusal_by_the_mapper_elsewhere_makes_a_bad_slot_not_judged(string reason, string breakage)
    {
        var findings = Check(Broken(breakage), ListRule, "'amdin' in @user.roles");

        findings.Where(IsError).ShouldHaveSingleItem().Message.ShouldContain("not judged", Case.Insensitive, reason);
    }

    [Theory]
    [MemberData(nameof(MapperRefusals))]
    public void A_refusal_by_the_mapper_elsewhere_makes_a_good_slot_not_judged_too(string reason, string breakage)
    {
        var findings = Check(Broken(breakage), ListRule, "'clerk' in @user.roles");

        findings.Where(IsError).ShouldHaveSingleItem().Message.ShouldContain("not judged", Case.Insensitive, reason);
    }

    private static JsonObject Broken(string breakage)
    {
        var descriptor = Descriptor();
        var entities = descriptor["entities"]!;
        var fields = entities["orders"]!["fields"]!;
        switch (breakage)
        {
            case "zz-int": fields["zz"] = JsonNode.Parse("{\"type\":\"integer\",\"default\":1e30}"); break;
            case "zz-enum": fields["zz"] = JsonNode.Parse("{\"type\":\"enum\",\"values\":[\"a\"],\"default\":\"b\"}"); break;
            default:
                entities["audited"] = JsonNode.Parse(
                    "{\"audit\":true,\"fields\":{\"created_at\":{\"type\":\"string\"}}}");
                break;
        }

        return descriptor;
    }

    [Fact]
    public void A_descriptor_without_a_schema_error_gets_no_not_judged_finding() =>
        Check(ListRule, "'amdin' in @user.roles").ShouldAllBe(f => !f.Message.Contains("not judged", StringComparison.Ordinal));

    [Fact]
    public void A_schema_error_at_the_slot_is_returned_as_is_without_a_not_judged_finding()
    {
        var findings = CheckWithSchemaErrorElsewhere(ListRule, new string('a', 2001));

        findings.Where(IsError).ShouldNotBeEmpty();
        findings.ShouldAllBe(f => !f.Message.Contains("not judged", StringComparison.Ordinal));
    }

    [Fact]
    public void The_not_judged_finding_names_at_most_three_places()
    {
        var descriptor = Descriptor();
        descriptor["title"] = new string('x', 61);
        descriptor["description"] = new string('y', 2000);
        descriptor["entities"]!["orders"]!["fields"]!["total"]!["precision"] = "wide";
        descriptor["entities"]!["orders"]!["fields"]!["unit_price"]!["precision"] = "wide";

        var message = Check(descriptor, ListRule, "true").Single(IsError).Message;

        message.ShouldContain("'/title'");
        message.Split("'/").Length.ShouldBe(4, "three quoted places, the fourth error is left out");
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
    public void A_mutate_value_the_schema_refuses_above_the_slot_is_reported_at_the_slot_not_as_not_judged()
    {
        var finding = ExpressionSlotCheck.Check(
                Validator(), DescriptorWithMutateHook().ToJsonString(), "/entities/orders/hooks/beforeCreate/0/action/mutate/status", "")
            .Single(IsError);

        finding.Path.ShouldBe("/entities/orders/hooks/beforeCreate/0/action/mutate/status");
        finding.Message.ShouldStartWith(ExpressionSlotCheck.SchemaRefusalPrefix);
    }

    [Theory]
    [InlineData("{\"entities\":{},\"entities\":{}}")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public void A_descriptor_that_is_not_a_json_object_is_refused(string descriptorJson) =>
        Should.Throw<ManagementRequestException>(() => ExpressionSlotCheck.Check(Validator(), descriptorJson, ListRule, "true"));

    [Fact]
    public void A_duplicate_key_on_the_path_to_a_valid_slot_is_refused_as_a_request_error_naming_the_duplicate()
    {
        var json = Descriptor().ToJsonString().Replace("\"list\":\"true\"", "\"list\":\"true\",\"list\":\"false\"", StringComparison.Ordinal);
        json.ShouldContain("\"list\":\"false\"", Case.Sensitive, "the fixture really carries a duplicate key");

        var refusal = Should.Throw<ManagementRequestException>(() => ExpressionSlotCheck.Check(Validator(), json, ListRule, "true"));

        refusal.Message.ShouldContain("duplicate property");
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{not json")]
    public void A_descriptor_that_is_not_an_object_is_refused_with_the_not_an_object_message(string descriptorJson)
    {
        var refusal = Should.Throw<ManagementRequestException>(() => ExpressionSlotCheck.Check(Validator(), descriptorJson, ListRule, "true"));

        refusal.Message.ShouldContain("not a JSON object");
    }

    [Fact]
    public void A_field_named_mutate_still_gets_a_string_splice()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["orders"]!["fields"]!["mutate"] =
            new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2, ["computed"] = "total" };

        Check(descriptor, "/entities/orders/fields/mutate/computed", "total * unit_price").Where(IsError).ShouldBeEmpty();
        Check(descriptor, "/entities/orders/fields/mutate/computed", "total *").Where(IsError).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("/entities/orders/hooks/beforeCreate/01/condition")]
    [InlineData("/entities/orders/hooks/beforeCreate/+0/condition")]
    [InlineData("/entities/orders/hooks/beforeCreate/ 0/condition")]
    public void An_array_index_outside_the_rfc_6901_grammar_is_refused(string jsonPointer)
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["orders"]!["hooks"] = new JsonObject
        {
            ["beforeCreate"] = new JsonArray(Hook(), Hook()),
        };

        Should.Throw<ManagementRequestException>(() => Check(descriptor, jsonPointer, "true"));
    }

    private static JsonObject Hook() => new() { ["condition"] = "true", ["action"] = new JsonObject { ["reject"] = "No." } };

    [Fact]
    public void A_pointer_with_escaped_segments_addresses_the_right_node()
    {
        // The schema forbids '/' in an entity key, so the descriptor is (rightly) not judged; what this pins is that
        // the escaped pointer RESOLVES to the node (no "does not exist" refusal) and the answer is about that slot.
        var descriptor = Descriptor();
        descriptor["entities"]!["a/b"] = descriptor["entities"]!["orders"]!.DeepClone();

        Check(descriptor, "/entities/a~1b/rules/list", "'clerk' in @user.roles").Where(IsError).Single()
            .Path.ShouldBe("/entities/a~1b/rules/list");
    }

    private static IReadOnlyList<DescriptorValidationError> CheckWithSchemaErrorElsewhere(string pointer, string source)
    {
        var descriptor = Descriptor();
        descriptor["title"] = new string('x', 61);

        return Check(descriptor, pointer, source);
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
