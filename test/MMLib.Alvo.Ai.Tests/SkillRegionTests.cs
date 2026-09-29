using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// Every marked region whose source is the schema or the ports equals it (D25). A failure prints the region as it
/// should read; the test never rewrites the file.
/// </summary>
public sealed class SkillRegionTests
{
    /// <summary>The regions this suite holds to the schema and the ports.</summary>
    private static readonly string[] _heldHere =
        ["entity-keys", "field-keys", "field-types", "on-delete", "built-in-formats", "managed-columns", "rollup-ops"];

    /// <summary>
    /// The regions whose source is the core, held in <c>MMLib.Alvo.Host.Tests</c>: the reserved fields by
    /// <c>SkillClaimTests</c>, the rest by <c>SkillCoreClaimsTests</c>.
    /// </summary>
    private static readonly string[] _heldInHostTests =
    [
        "reserved-fields", "cel-rule", "cel-condition", "cel-mutate", "mutate-functions", "cel-computed", "cel-access",
        "honoured", "warned", "refused-actions",
    ];

    [Fact]
    public void Every_region_a_skill_declares_is_held_to_a_source() =>
        SkillCatalogue.RegionIds().ShouldBe(_heldHere.Concat(_heldInHostTests), ignoreOrder: true);

    [Fact]
    public void The_entity_keys_are_the_schemas() =>
        Holds("entities-and-fields", "entity-keys", Keys(Schema()["$defs"]!["entity"]!["properties"]!));

    [Fact]
    public void The_field_keys_are_the_schemas() =>
        Holds("entities-and-fields", "field-keys", Keys(Schema()["$defs"]!["field"]!["properties"]!));

    [Fact]
    public void The_field_types_are_the_schemas() =>
        Holds("field-types-and-formats", "field-types", Enum(Schema()["$defs"]!["fieldType"]!["enum"]!));

    [Fact]
    public void The_on_delete_behaviours_are_the_schemas() =>
        Holds("field-types-and-formats", "on-delete", Enum(Schema()["$defs"]!["field"]!["properties"]!["onDelete"]!["enum"]!));

    [Fact]
    public void The_built_in_formats_are_the_schemas() =>
        Holds("field-types-and-formats", "built-in-formats", Enum(Schema()["$defs"]!["field"]!["properties"]!["format"]!["anyOf"]![0]!["enum"]!));

    [Fact]
    public void The_rollup_operations_are_the_schemas() =>
        Holds("computed-and-rollups", "rollup-ops", Enum(Schema()["$defs"]!["field"]!["properties"]!["rollup"]!["properties"]!["op"]!["enum"]!));

    [Fact]
    public void The_managed_columns_per_trait_are_the_ones_the_framework_injects()
    {
        var region = Region("traits-and-tenancy", "managed-columns");
        var stated = InstructionClaims.ManagedColumns(region);
        var always = AlvoManagedColumns.For(null, audit: false, softDelete: false);

        stated.Keys.ShouldBe(InstructionClaims.TraitLabels);
        stated[InstructionClaims.EveryEntity].ShouldBe(always, ignoreOrder: true);
        stated[InstructionClaims.AuditedEntity].ShouldBe(AlvoManagedColumns.Audit);
        stated.Values.SelectMany(columns => columns)
            .ShouldBe(AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true), ignoreOrder: true);
    }

    [Fact]
    public void The_managed_columns_region_is_the_base_prompts_bullets_verbatim() =>
        AssistantInstructionsText().ShouldContain(Region("traits-and-tenancy", "managed-columns"), Case.Sensitive);

    private static void Holds(string area, string region, IReadOnlyList<string> source) =>
        SkillCatalogue.Tokens(Region(area, region)).ShouldBe(source, $"<!-- gen:{region} --> should read:\n{SkillCatalogue.Expected(source)}");

    private static string Region(string area, string region)
    {
        var regions = SkillCatalogue.Regions(SkillCatalogue.Named(area).Body);

        regions.ShouldContainKey(region, $"alvo-descriptor-{area} has no <!-- gen:{region} --> region");
        return regions[region];
    }

    private static string AssistantInstructionsText() => Internal.AssistantInstructions.Text.ReplaceLineEndings("\n");

    private static List<string> Keys(JsonNode members) => [.. members.AsObject().Select(member => member.Key)];

    private static List<string> Enum(JsonNode values) => [.. values.AsArray().Select(value => value!.GetValue<string>())];

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
}
