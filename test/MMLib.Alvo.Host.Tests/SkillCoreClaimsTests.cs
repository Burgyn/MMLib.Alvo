using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The skill regions whose source is the core: each CEL profile's stated examples compile, or are refused, under that
/// profile (D35); the Mutate functions, and the capability names (D27), equal the tables that define them.
/// </summary>
/// <remarks>
/// <para>
/// The CEL table (<c>CelTypeChecker._allowedProfiles</c>) and its key are private, so the regions are held by probing
/// <see cref="CelCompiler"/>, as the Computed quotes are. That proves every claim; it does not prove the list complete,
/// and it is not meant to — a skill teaches the common constructs, and a construct it omits is refused by the tool.
/// </para>
/// <para>
/// <b>A refused example must compile under some other profile.</b> Otherwise it could be refused for a typo or a type
/// error, and the region would teach "this profile refuses X" on the strength of X refusing everywhere.
/// </para>
/// </remarks>
public sealed class SkillCoreClaimsTests
{
    private const string Project = "<project>";

    private static readonly (string Area, string Region, CelProfile Profile, string Entity)[] _profiles =
    [
        ("rules-and-cel", "cel-rule", CelProfile.Rule, "technicians"),
        ("hooks", "cel-condition", CelProfile.Condition, "order_lines"),
        ("hooks", "cel-mutate", CelProfile.Mutate, "order_lines"),
        ("computed-and-rollups", "cel-computed", CelProfile.Computed, "order_lines"),
        ("project-access", "cel-access", CelProfile.Access, Project),
    ];

    private static readonly string[] _mutateFunctions = [CelCall.LowerAscii, CelCall.Now];

    /// <summary>
    /// The field each allowed <c>cel-computed</c> example is declared on for the real dry run. The compiler is not the
    /// whole Computed authority (the validator's own computed check refuses some expressions it compiles), so every
    /// allowed example is also run as a computed field of <c>order_lines</c>; a new example needs a row here.
    /// </summary>
    private static readonly Dictionary<string, string> _computedFields = new(StringComparer.Ordinal)
    {
        ["quantity * unit_price"] = """{"type": "decimal", "precision": 16, "scale": 4, "computed": "quantity * unit_price"}""",
        ["-unit_price"] = """{"type": "decimal", "precision": 8, "scale": 2, "computed": "-unit_price"}""",
        ["description + ' ' + kind"] = """{"type": "string", "computed": "description + ' ' + kind"}""",
    };

    public static TheoryData<int> Profiles() => [.. Enumerable.Range(0, _profiles.Length)];

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Every_stated_cel_example_compiles_exactly_where_its_skill_says(int row)
    {
        var (area, region, profile, entity) = _profiles[row];
        var entities = await EntitiesAsync();
        var lines = Region(area, region).Split('\n');

        Claims(lines, "- allowed:").ShouldAllBe(source => Compiles(source, profile, entities[entity]), $"{region}: allowed");
        Claims(lines, "- refused:").ShouldAllBe(source => !Compiles(source, profile, entities[entity]), $"{region}: refused");
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Every_refused_cel_example_compiles_under_a_profile_that_allows_it(int row)
    {
        var (area, region, profile, entity) = _profiles[row];
        var entities = await EntitiesAsync();
        var others = Enum.GetValues<CelProfile>().Where(other => other != profile).ToList();

        Claims(Region(area, region).Split('\n'), "- refused:").ShouldAllBe(
            source => others.Any(other => Compiles(source, other, entities[EntityFor(other, entity)])),
            $"{region}: a refused example is refused everywhere, so it proves nothing about {profile}");
    }

    [Fact]
    public async Task Every_allowed_computed_example_passes_the_real_dry_run_as_a_computed_field()
    {
        var allowed = Claims(Region("computed-and-rollups", "cel-computed").Split('\n'), "- allowed:");
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        _computedFields.Keys.ShouldBe(allowed, ignoreOrder: true);
        foreach (var source in allowed)
        {
            var attempt = await InstructionExampleOutcomeTests.AttemptAsync(management, "/entities/order_lines/fields/probe_computed", _computedFields[source]);
            attempt.Valid.ShouldBeTrue($"{source}: {string.Join(" | ", attempt.Refusals)}");
        }
    }

    [Fact]
    public void The_mutate_functions_are_the_ones_the_profile_allow_lists() =>
        SkillCatalogue.Tokens(Region("hooks", "mutate-functions")).ShouldBe(_mutateFunctions);

    [Fact]
    public void The_honoured_blocks_are_the_capability_reports() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "honoured")).ShouldBe(CapabilityReport.Honoured);

    [Fact]
    public void The_warned_blocks_are_the_unhonoured_subsystems() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "warned")).ShouldBe(
            UnhonouredSubsystems.All.Concat(UnhonouredSubsystems.WithinBlocks).Concat(UnhonouredSubsystems.ReportedOnly).Select(block => block.Block));

    [Fact]
    public void The_refused_slots_are_the_unhonoured_features() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "refused")).ShouldBe(
            UnhonouredFeatures.EveryRefusal.Select(refusal => refusal.Slot), ignoreOrder: true);

    [Fact]
    public void The_refused_region_ends_with_the_refused_action_types() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "refused")).TakeLast(UnhonouredFeatures.EveryActionType.Count)
            .ShouldBe(UnhonouredFeatures.EveryActionType);

    /// <summary>
    /// The entity another profile is tried against: the region's own, so its fields resolve, except that an access level
    /// sees no row at all, and the access region's examples are tried against the entity the rule region reads.
    /// </summary>
    private static string EntityFor(CelProfile profile, string entity) =>
        profile == CelProfile.Access ? Project : entity == Project ? _profiles[0].Entity : entity;

    private static bool Compiles(string source, CelProfile profile, EntitySchema entity) =>
        new CelCompiler().Compile(source, profile, entity).IsSuccess;

    private static async Task<Dictionary<string, EntitySchema>> EntitiesAsync()
    {
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var entities = world.Services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        entities[Project] = new EntitySchema { Name = Project, Fields = [] };
        return entities;
    }

    private static string Region(string area, string region)
    {
        var regions = SkillCatalogue.Regions(SkillCatalogue.Named(area).Body);

        regions.ShouldContainKey(region, $"alvo-descriptor-{area} has no <!-- gen:{region} --> region");
        return regions[region];
    }

    private static List<string> Claims(string[] lines, string prefix) =>
        [.. SkillCatalogue.Tokens(lines.Single(line => line.StartsWith(prefix, StringComparison.Ordinal)))];
}
