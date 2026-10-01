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
            new ManagementWarnedBlock("dynamicEntities", "no runtime entity can be created"),
            new ManagementWarnedBlock("automation", "no rule is ever evaluated"),
            new ManagementWarnedBlock("webhooks", "an after-hook delivers; automation does not"),
            new ManagementWarnedBlock("functions", "no function is ever invoked"),
            new ManagementWarnedBlock("auth.providers", "only local credentials exist"),
            new ManagementWarnedBlock("entity.storage", "a dynamic entity is not created"),
            new ManagementWarnedBlock("entity.realtime", "no change is published"),
        ],
        []);

    [Fact]
    public void Only_the_warned_blocks_the_descriptor_declares_are_listed()
    {
        var declared = DeclaredLimits.Of(
            """{ "entities": {}, "webhooks": { "endpoints": { "erp": {} } }, "functions": { "f": {} } }""", _capabilities);

        declared.Select(block => block.Block).ShouldBe(
            ["webhooks", "functions"],
            "a block nobody declared has nothing to warn about, and the build's order is kept");
    }

    [Fact]
    public void The_consequence_is_the_frameworks_own_sentence()
    {
        var declared = DeclaredLimits.Of("""{ "webhooks": { "endpoints": { "erp": {} } } }""", _capabilities);

        declared.ShouldHaveSingleItem().Consequence.ShouldBe("an after-hook delivers; automation does not");
    }

    /// <summary>
    /// <b>A block declined by value is not a declaration</b> — the core's own rule for its warning
    /// (<c>UnhonouredSubsystems</c>), and the Overview used to list <c>dynamicEntities</c> for
    /// <c>enabled: false</c> (docs/todo-admin.md §5e 13, #271).
    /// </summary>
    [Theory]
    [InlineData("""{ "dynamicEntities": { "enabled": false, "maxEntitiesPerTenant": 5 } }""")]
    [InlineData("""{ "dynamicEntities": { "maxEntitiesPerTenant": 5 } }""")]
    [InlineData("""{ "automation": {} }""")]
    [InlineData("""{ "webhooks": { "endpoints": {} } }""")]
    [InlineData("""{ "functions": {} }""")]
    public void A_block_declined_by_value_is_not_listed(string descriptor)
        => DeclaredLimits.Of(descriptor, _capabilities).ShouldBeEmpty();

    [Fact]
    public void Dynamic_entities_enabled_is_listed()
        => Slots("""{ "dynamicEntities": { "enabled": true } }""").ShouldBe(["dynamicEntities"]);

    /// <summary>A <c>storage: dynamic</c> entity names the slot the build warns under (§8d item 21).</summary>
    [Fact]
    public void A_dynamic_entity_lists_the_storage_slot()
        => Slots("""{ "entities": { "notes": { "storage": "dynamic", "realtime": false } } }""")
            .ShouldBe(["entity.storage"]);

    [Fact]
    public void A_physical_entity_with_nothing_declared_lists_nothing()
        => Slots("""{ "entities": { "notes": { "storage": "physical" } } }""").ShouldBeEmpty();

    /// <summary>A provider other than <c>local</c> names the provider slot, and <c>local</c> alone does not (§8d item 27).</summary>
    [Theory]
    [InlineData("""{ "auth": { "providers": ["local", "google"] } }""", true)]
    [InlineData("""{ "auth": { "providers": ["local"], "roles": ["clerk"] } }""", false)]
    public void Only_a_provider_other_than_local_lists_the_provider_slot(string descriptor, bool listed)
        => Slots(descriptor).Contains("auth.providers").ShouldBe(listed);

    /// <summary>
    /// <c>realtime</c> is listed only where an entity declares <c>realtime: true</c>: the Overview says what the
    /// project declared, and the default is said once on Settings, under this build (B2 review, finding 2).
    /// </summary>
    [Theory]
    [InlineData("""{ "entities": { "notes": {} } }""", false)]
    [InlineData("""{ "entities": { "notes": { "realtime": true } } }""", true)]
    [InlineData("""{ "entities": { "notes": { "realtime": false } } }""", false)]
    [InlineData("""{ "entities": {} }""", false)]
    public void Realtime_is_listed_only_where_an_entity_declares_it(string descriptor, bool listed)
        => Slots(descriptor).Contains("entity.realtime").ShouldBe(listed);

    /// <summary>
    /// The project's own metadata this dashboard does not render is one quiet line, the dashboard's sentence and not
    /// the build's: the build honours metadata by definition (<c>CapabilityReport</c>), so it is not in the panel that
    /// says what the build does not honour (B2 review, finding 1).
    /// </summary>
    [Fact]
    public void Project_metadata_this_dashboard_does_not_show_is_one_line()
        => DeclaredLimits.UnshownLine(
                """{ "description": "A workshop.", "branding": { "title": "Bikes" }, "formats": { "sku": { "pattern": "^S", "description": "A stock unit." } } }""")
            .ShouldBe("Declared metadata this dashboard does not show yet: description, branding, formats.*.description (#268, #271).");

    [Fact]
    public void The_line_names_only_the_issues_of_what_is_declared()
        => DeclaredLimits.UnshownLine("""{ "branding": { "title": "Bikes" } }""")
            .ShouldBe("Declared metadata this dashboard does not show yet: branding (#268).");

    [Fact]
    public void Metadata_that_is_absent_or_empty_is_not_said()
        => DeclaredLimits.UnshownLine("""{ "description": "", "branding": {}, "formats": { "sku": { "pattern": "^S" } } }""")
            .ShouldBeNull();

    /// <summary>Every qualified slot this screen detects, which a Host test holds against the real build's report.</summary>
    [Fact]
    public void The_qualified_slots_are_the_three_the_build_reports()
        => DeclaredSlots.Qualified.ShouldBe(["auth.providers", "entity.storage", "entity.realtime"], ignoreOrder: true);

    [Fact]
    public void A_descriptor_that_does_not_parse_declares_nothing()
        => DeclaredLimits.Of("{ not json", _capabilities).ShouldBeEmpty();

    /// <summary>The slots of the rows <see cref="DeclaredLimits.Of"/> lists for one descriptor.</summary>
    private static IReadOnlyList<string> Slots(string descriptor)
        => [.. DeclaredLimits.Of(descriptor, _capabilities).Select(block => block.Block)];

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
