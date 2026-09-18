using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// <b>Capabilities may not lie.</b> The payload is compared against the two tables themselves, in the same
/// shape that already guards those tables against the frozen schema — so a subsystem that lands, and leaves
/// <see cref="UnhonouredSubsystems"/>, changes this endpoint with nobody editing it.
/// </summary>
public class ManagementCapabilitiesTests
{
    [Fact]
    public void Warned_is_every_unhonoured_subsystem_and_nothing_else()
    {
        var warned = CapabilityReport.Project().Warned;

        warned.Select(block => block.Block).ShouldBe(UnhonouredSubsystems.All.Select(block => block.Block));
    }

    [Fact]
    public void Every_warned_consequence_is_the_table_s_own_sentence_character_for_character()
    {
        foreach (var (served, declared) in CapabilityReport.Project().Warned.Zip(UnhonouredSubsystems.All))
        {
            served.Consequence.ShouldBe(
                declared.Consequence,
                $"'{declared.Block}' must be served as written; a second wording is a third spelling of one truth");
        }
    }

    /// <summary>
    /// Every refusal the framework can make reaches the report, and each carries a fix.
    /// </summary>
    /// <remarks>
    /// <b>Counted against <see cref="UnhonouredFeatures.EveryFixSuggestion"/> rather than against
    /// <see cref="UnhonouredFeatures.EveryRefusal"/>.</b> Comparing the report to the enumeration it is
    /// projected from would be an equality between a thing and itself; that other enumeration is built from
    /// the same four sources by different code and is already relied on elsewhere, so it is the independent
    /// reading a tie needs.
    /// </remarks>
    [Fact]
    public void Every_refusal_the_framework_can_make_reaches_the_report_with_its_fix()
    {
        var refused = CapabilityReport.Project().Refused;

        refused.Count.ShouldBe(
            UnhonouredFeatures.EveryFixSuggestion.Count,
            "a refusal the apply path can make and this report cannot name is a refusal nobody was warned "
            + "about, which is the whole failure mode the endpoint exists to close");
        refused.Select(feature => feature.Slot).ShouldBeUnique();
        refused.ShouldAllBe(feature => feature.Fix.Length > 0, "a refusal without a fix is a dead end");
    }

    [Fact]
    public void Refused_names_the_slots_the_reference_drawing_already_uses()
    {
        var slots = CapabilityReport.Project().Refused.Select(feature => feature.Slot).ToList();

        slots.ShouldContain("field.default");
        slots.ShouldContain("field.validation");
        slots.ShouldContain("entity.softDelete");
    }

    [Fact]
    public void Honoured_and_warned_never_name_the_same_block()
    {
        CapabilityReport.Honoured
            .Intersect(UnhonouredSubsystems.All.Select(block => block.Block), StringComparer.Ordinal)
            .ShouldBeEmpty("a block cannot be both honoured and warned about; one of the two lists is stale");
    }

    /// <summary>
    /// Every honoured name is a top-level block the frozen schema declares.
    /// </summary>
    /// <remarks>
    /// The same check <c>UnhonouredSubsystemsTests.Every_unhonoured_subsystem_names_a_block_the_schema
    /// _declares</c> applies to the other table, and for the identical reason: a name no descriptor can
    /// carry — <c>rules</c> and <c>hooks</c> are per-entity, not top-level — reads as coverage while
    /// describing nothing.
    /// </remarks>
    [Fact]
    public void Every_honoured_block_is_one_the_schema_declares()
    {
        var schema = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
        var declared = schema["properties"]!.AsObject().Select(property => property.Key).ToList();

        CapabilityReport.Honoured.ShouldBeSubsetOf(
            declared,
            "an honoured name that is not a top-level block claims coverage of something no descriptor "
            + "declares");
    }
}
