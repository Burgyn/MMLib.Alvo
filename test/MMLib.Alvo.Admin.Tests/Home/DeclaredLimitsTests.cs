using MMLib.Alvo.Admin.Components.Home;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Home;

/// <summary>
/// Which warned blocks Overview lists, and the counts beside its links.
/// </summary>
public class DeclaredLimitsTests
{
    private static readonly ManagementCapabilities _capabilities = new(
        ["entities"],
        [
            new ManagementWarnedBlock("automation", "no rule is ever evaluated"),
            new ManagementWarnedBlock("webhooks", "an after-hook delivers; automation does not"),
            new ManagementWarnedBlock("functions", "no function is ever invoked"),
        ],
        []);

    [Fact]
    public void Only_the_warned_blocks_the_descriptor_declares_are_listed()
    {
        var declared = DeclaredLimits.Of("""{ "entities": {}, "webhooks": {}, "functions": {} }""", _capabilities);

        declared.Select(block => block.Block).ShouldBe(
            ["webhooks", "functions"],
            "a block nobody declared has nothing to warn about, and the build's order is kept");
    }

    [Fact]
    public void The_consequence_is_the_frameworks_own_sentence()
    {
        var declared = DeclaredLimits.Of("""{ "webhooks": {} }""", _capabilities);

        declared.ShouldHaveSingleItem().Consequence.ShouldBe("an after-hook delivers; automation does not");
    }

    [Fact]
    public void A_descriptor_that_does_not_parse_declares_nothing()
        => DeclaredLimits.Of("{ not json", _capabilities).ShouldBeEmpty();

    [Fact]
    public void Rules_are_counted_over_every_entity_and_operation()
    {
        const string descriptor = """
            {
              "entities": {
                "customers": { "rules": { "list": "true", "get": "true" } },
                "orders": { "rules": { "create": "'admin' in @user.roles" } },
                "notes": { }
              }
            }
            """;

        DescriptorLens.RuleCount(descriptor).ShouldBe(3);
    }

    [Fact]
    public void A_descriptor_with_no_entities_has_no_rules()
        => DescriptorLens.RuleCount("{}").ShouldBe(0);
}
