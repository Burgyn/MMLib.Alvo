using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// The generator is a fixture, and these are the facts that make a broken fixture fail as itself.
/// </summary>
/// <remarks>
/// Without them a generator bug arrives as sixteen mysterious API failures, and the first instinct on
/// reading those is to look at the API. Validity, determinism and coverage are asserted before any invariant
/// runs over a single generated descriptor.
/// </remarks>
public class GeneratedProjectTests
{
    /// <summary>The sixteen committed seeds as theory data, so each case is named by its seed.</summary>
    public static TheoryData<int> Seeds => [.. GeneratedProject.Seeds];

    /// <summary>Every generated descriptor is one the framework itself accepts.</summary>
    /// <remarks>
    /// <c>DescriptorValidator</c> is the production path, and it is strictly stronger than validating
    /// against <c>schema/project.schema.json</c> alone: it also enforces the reserved-query-name and
    /// managed-column rules the schema cannot express, and it compiles every CEL rule. So this is the same
    /// judgement an apply would make, taken without booting anything.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_generated_descriptor_is_one_the_framework_accepts(int seed)
    {
        var project = GeneratedProject.Generate(seed);

        var result = new DescriptorValidator().Validate(project.Json);

        result.Errors.ShouldBeEmpty(
            $"seed {seed} generated a descriptor the framework refuses:{Environment.NewLine}"
            + $"{string.Join(Environment.NewLine, result.Errors.Select(error => $"  {error.Path}: {error.Message}"))}"
            + $"{Environment.NewLine}{project.Json}");
    }

    /// <summary>The same seed generates the same descriptor, byte for byte.</summary>
    /// <remarks>
    /// The whole reason the seeds are committed rather than random: a case that cannot be reproduced is a
    /// case nobody can fix. Asserted rather than assumed because it rests on a measured property of
    /// <c>PCG</c>'s two-argument constructor.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Generation_is_deterministic(int seed) =>
        GeneratedProject.Generate(seed).Json.ShouldBe(
            GeneratedProject.Generate(seed).Json, $"seed {seed} must be reproducible");

    /// <summary>
    /// The corpus covers every field type the frozen schema declares, both tenancy settings, a nullable
    /// field, and all three rule roles.
    /// </summary>
    /// <remarks>
    /// <b>The expected set is read out of <c>schema/project.schema.json</c></b>, not restated here, so
    /// "covers every field type" cannot be satisfied by a generator that emits three types and a test that
    /// asks about three types — and a twelfth type added to the schema fails this fact until the generator
    /// draws it.
    /// </remarks>
    [Fact]
    public void The_corpus_covers_every_field_type_both_tenancy_settings_and_all_three_rule_roles()
    {
        var projects = GeneratedProject.Seeds.Select(GeneratedProject.Generate).ToList();

        var drawn = projects
            .SelectMany(project => project.Fields.Values)
            .SelectMany(fields => fields.Select(field => field.Value!["type"]!.GetValue<string>()))
            .ToHashSet(StringComparer.Ordinal);

        drawn.ShouldBe(SchemaFieldTypes(), ignoreOrder: true, "the corpus must exercise every declared field type");
        projects.ShouldContain(project => project.Tenant != null, "no generated project enables tenancy");
        projects.ShouldContain(project => project.Tenant == null, "every generated project enables tenancy");
        projects.ShouldAllBe(
            project => project.PermissiveEntities.Count > 0
                && project.DeniedEntities.Count > 0
                && project.PartialEntities.Count > 0,
            "every case must reach all three rule roles: CRUD, default-deny and per-operation default-deny");

        // `nullable` is the facet this PR's own document fix is about, so a corpus that never drew one
        // could not have hardened it — asserted rather than assumed.
        projects
            .SelectMany(project => project.Fields.Values)
            .SelectMany(fields => fields.Select(field => field.Value!.AsObject()))
            .ShouldContain(field => field.ContainsKey("nullable"), "no generated field is nullable");
    }

    /// <summary>No generated field name shadows a reserved query parameter or a managed column.</summary>
    /// <remarks>
    /// Asked of the framework's own <c>ReservedQueryKeys</c> rather than compared against a list copied into
    /// the generator, so the field-name pool cannot drift out of step with what the validator enforces. The
    /// validity fact above would catch a collision too — this one says which pool entry caused it.
    /// </remarks>
    [Fact]
    public void No_generated_field_name_is_reserved() =>
        GeneratedProject.Seeds
            .Select(GeneratedProject.Generate)
            .SelectMany(project => project.Fields.Values)
            .SelectMany(fields => fields.Select(field => field.Key))
            .Distinct(StringComparer.Ordinal)
            .ShouldAllBe(name => !ReservedQueryKeys.IsReserved(name));

    /// <summary>The eleven field types <c>schema/project.schema.json</c> declares, read off the file.</summary>
    private static HashSet<string> SchemaFieldTypes()
    {
        var schema = JsonNode.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;

        return [.. schema["$defs"]!["fieldType"]!["enum"]!.AsArray().Select(type => type!.GetValue<string>())];
    }
}
