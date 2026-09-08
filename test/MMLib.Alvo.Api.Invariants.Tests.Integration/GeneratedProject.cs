using CsCheck;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>One generated Alvo project: a descriptor, and everything a fact needs to drive it.</summary>
/// <param name="Seed">The seed that produced it, which is also how a failure is reproduced.</param>
/// <param name="Name">The project name, and the temp file's stem.</param>
/// <param name="Json">The descriptor.</param>
/// <param name="Entities">Every entity, in declaration order.</param>
/// <param name="PermissiveEntities">The entities whose rules admit every caller.</param>
/// <param name="DeniedEntities">The entities that declare no rules at all, and so are refused to everyone.</param>
/// <param name="Fields">Each entity's declared fields, so a payload can be built from what it actually has.</param>
/// <param name="Tenant">The tenant every request is made in, or <see langword="null"/> where tenancy is off.</param>
/// <param name="ScopedEntities">
/// The entities whose rows carry a tenant. A create on one of these has to <em>echo</em> the caller's
/// <c>tenant_id</c> in the body — the server verifies it rather than filling it in, which is what the create
/// schema publishes and what <c>DataApiAuthTests</c> already relies on — while a replace refuses the member
/// outright. Nothing else in this suite needs to know, and both halves of that asymmetry are load-bearing.
/// </param>
internal sealed record GeneratedProject(
    int Seed,
    string Name,
    string Json,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> PermissiveEntities,
    IReadOnlyList<string> DeniedEntities,
    IReadOnlyDictionary<string, JsonObject> Fields,
    Guid? Tenant,
    IReadOnlySet<string> ScopedEntities)
{
    /// <summary>
    /// The sixteen committed seeds — the corpus every invariant runs over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Committed rather than random, deliberately.</b> A random-seeded property suite on a required PR
    /// check is a flake generator: it turns red for a case nobody changed and, worse, the case that failed is
    /// gone by the time anyone looks. Sixteen fixed seeds give a corpus that is reproducible by name, and
    /// <c>ALVO_INVARIANT_N</c> / <c>ALVO_INVARIANT_SEED</c> widen or move it for a deliberate exploratory run.
    /// </para>
    /// <para>
    /// <b>CsCheck is used as a deterministic case generator, not as a shrinking property runner</b>, which is
    /// a deviation from spec §286's "property-based" wording and worth stating. <c>Sample</c> would shrink a
    /// falsifying descriptor, but every case here boots a host and applies a schema, so a shrink costs
    /// seconds per step — and what a shrink would buy (a smaller descriptor to read) is bought instead by
    /// printing the descriptor with the failure and naming the seed in the test case.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<int> Seeds { get; } = BuildSeeds();

    /// <summary>Generates the project for one seed, always identically.</summary>
    /// <param name="seed">The seed.</param>
    /// <remarks>
    /// <c>new PCG(stream, state)</c> is the deterministic constructor — measured: the two-argument form
    /// yields the same value for the same arguments, while the single-argument one does not. Every draw
    /// below comes off this one generator in a fixed order, so the descriptor is a pure function of the seed.
    /// </remarks>
    internal static GeneratedProject Generate(int seed)
    {
        var pcg = new PCG(1, (uint)seed);

        var tenancy = Draw(Gen.Bool, pcg);
        var names = Draw(Gen.OneOfConst(_entityNames).ArrayUnique[Draw(Gen.Int[2, 3], pcg)], pcg);
        var entities = new JsonObject();
        var fields = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var permissive = new List<string>();
        var denied = new List<string>();
        var scoped = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < names.Length; index++)
        {
            // The first entity is always permissive and the second always unconfigured, so both the CRUD
            // invariants and the default-deny one are reachable in every single case rather than in most.
            var admits = index switch { 0 => true, 1 => false, _ => Draw(Gen.Bool, pcg) };
            var declared = DeclaredFields(names, index, pcg);

            var entity = Entity(declared, admits, tenancy, pcg);
            entities[names[index]] = entity;
            fields[names[index]] = declared;
            (admits ? permissive : denied).Add(names[index]);
            if (entity["tenancy"]?.GetValue<string>() == "scoped")
            {
                scoped.Add(names[index]);
            }
        }

        var tenant = tenancy ? Guid.Parse($"00000000-0000-0000-0000-{seed:D12}") : (Guid?)null;

        return new GeneratedProject(
            seed,
            $"generated-{seed:D2}",
            Descriptor($"generated-{seed:D2}", entities, tenancy).ToJsonString(_indented),
            names,
            permissive,
            denied,
            fields,
            tenant,
            scoped);
    }

    /// <summary>Writes the descriptor to a temp file and starts a world over it.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    /// <param name="setup">Anything the world's host is configured differently from the default.</param>
    internal async Task<AlvoApiWorld> StartAsync(
        IReadOnlyList<TestApiKey> keys, AlvoApiWorldSetup? setup = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "alvo-invariants");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Name}.alvo.json");
        await File.WriteAllTextAsync(path, Json);

        return await AlvoApiWorld.FromDescriptorPathAsync(path, keys, setup);
    }

    /// <summary>A key that carries every scope, and this project's tenant where it has one.</summary>
    /// <param name="keyId">The key's identifier.</param>
    internal TestApiKey Admin(string keyId = "admin-key") =>
        new(keyId, ["admin", "authenticated"], ["*:read", "*:write"], Tenant);

    /// <summary>Indented, because a failure prints the descriptor and a reader has to read it.</summary>
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    /// <summary>
    /// Entity names a real descriptor would carry, including multi-word snake_case ones.
    /// </summary>
    /// <remarks>
    /// <c>work_orders</c> and <c>invoice_items</c> are in the pool on purpose: they are the shape that makes
    /// <c>paths-kebab-case</c> fire, which is why the ruleset turns that rule off (PostgREST parity — the path
    /// segment is the identifier verbatim). A corpus of single-word names would have hidden that entirely.
    /// No name is an HTTP verb, because <c>no-http-verbs-in-path</c> stays on and this suite is here to
    /// measure Alvo rather than a hypothetical user's naming.
    /// </remarks>
    private static readonly string[] _entityNames =
    [
        "owners", "vehicles", "work_orders", "invoice_items", "customers", "regions", "projects",
        "contacts", "deals", "assets", "shipments", "line_items",
    ];

    /// <summary>
    /// Field names that are neither a reserved query parameter nor a framework-managed column.
    /// </summary>
    /// <remarks>
    /// The exclusion is asserted against the framework's own <c>ReservedQueryKeys</c> in
    /// <c>GeneratedProjectTests</c> rather than trusted here, so this pool cannot quietly drift out of step
    /// with the list the descriptor validator actually enforces.
    /// </remarks>
    private static readonly string[] _fieldNames =
    [
        "name", "code", "note", "amount", "quantity", "colour", "status", "started_on", "finished_at",
        "reference", "payload", "is_active", "external_id", "weight", "priority",
    ];

    /// <summary>Every field type the frozen schema declares.</summary>
    private static readonly string[] _fieldTypes =
    [
        "string", "text", "integer", "decimal", "boolean", "date", "datetime", "uuid", "json", "enum", "ref",
    ];

    /// <summary>The seed set, honouring the two environment overrides.</summary>
    private static IReadOnlyList<int> BuildSeeds()
    {
        var first = Environment.GetEnvironmentVariable("ALVO_INVARIANT_SEED") is { Length: > 0 } seed
            ? int.Parse(seed, CultureInfo.InvariantCulture)
            : 1;
        var count = Environment.GetEnvironmentVariable("ALVO_INVARIANT_N") is { Length: > 0 } n
            ? int.Parse(n, CultureInfo.InvariantCulture)
            : 16;

        return [.. Enumerable.Range(first, count)];
    }

    /// <summary>The descriptor envelope every generated project shares.</summary>
    private static JsonObject Descriptor(string name, JsonObject entities, bool tenancy)
    {
        var descriptor = new JsonObject
        {
            ["$schema"] = "https://alvo.dev/schema/v1/project.json",
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = name,
            ["description"] =
                "Generated by MMLib.Alvo.Api.Invariants.Tests.Integration (#26). Not a fixture to edit: "
                + "regenerate it from its seed.",
            ["auth"] = new JsonObject { ["providers"] = new JsonArray("local") },
            ["entities"] = entities,
        };

        if (tenancy)
        {
            descriptor["tenancy"] = new JsonObject { ["enabled"] = true };
        }

        return descriptor;
    }

    /// <summary>One entity: its fields, its rules, and the framework-managed column switch.</summary>
    /// <remarks>
    /// <b><c>softDelete</c> is deliberately never drawn.</b> The schema declares it and this build refuses it
    /// at apply — "a delete would remove the row outright and reads would not exclude it, which is
    /// irrecoverable data loss where the schema promises recoverability" — so a corpus that drew it would be
    /// a corpus of descriptors the framework rejects. It was drawn in the first version of this generator and
    /// <c>A_generated_descriptor_is_one_the_framework_accepts</c> is what said so, which is the whole reason
    /// that fact runs before any invariant does.
    /// </remarks>
    private static JsonObject Entity(JsonObject fields, bool admits, bool tenancy, PCG pcg)
    {
        var entity = new JsonObject
        {
            ["description"] = "A generated entity.",
            ["audit"] = Draw(Gen.Bool, pcg),
            ["fields"] = fields,
        };

        if (tenancy)
        {
            entity["tenancy"] = Draw(Gen.Bool, pcg) ? "scoped" : "global";
        }

        if (admits)
        {
            // Literally "true" rather than a role expression, and that is load-bearing: Sabotage
            // substitutes one entity's decision for another's, and a predicate naming a column the other
            // entity does not have would fail for a reason that is not the sabotage.
            entity["rules"] = new JsonObject
            {
                ["list"] = "true",
                ["get"] = "true",
                ["create"] = "true",
                ["update"] = "true",
                ["delete"] = "true",
            };
        }

        return entity;
    }

    /// <summary>One entity's fields, honouring every conditional the frozen schema declares.</summary>
    private static JsonObject DeclaredFields(string[] names, int index, PCG pcg)
    {
        var chosen = Draw(Gen.OneOfConst(_fieldNames).ArrayUnique[Draw(Gen.Int[1, 5], pcg)], pcg);
        var fields = new JsonObject();

        for (var position = 0; position < chosen.Length; position++)
        {
            // A `ref` needs a target that already exists, so it is only ever offered from the second entity
            // on — the first entity has nothing to point at.
            var types = index == 0 ? _fieldTypes[..^1] : _fieldTypes;
            var type = Draw(Gen.OneOfConst(types), pcg);

            // The first field of a permissive entity is always a required string, so a payload always has
            // one predictable member to write and to read back.
            fields[chosen[position]] = position == 0
                ? new JsonObject { ["type"] = "string", ["required"] = true, ["maxLength"] = 120 }
                : Field(type, names[0], pcg);
        }

        return fields;
    }

    /// <summary>One field, with the members its type requires and none it forbids.</summary>
    private static JsonObject Field(string type, string refTarget, PCG pcg)
    {
        var field = new JsonObject { ["type"] = type, ["required"] = Draw(Gen.Bool, pcg) };

        switch (type)
        {
            case "string":
                field["maxLength"] = Draw(Gen.Int[8, 200], pcg);
                break;
            case "decimal":
                var precision = Draw(Gen.Int[4, 18], pcg);
                field["precision"] = precision;
                field["scale"] = Draw(Gen.Int[0, 3], pcg);
                break;
            case "enum":
                field["values"] = new JsonArray("open", "closed", "pending");
                break;
            case "ref":
                field["entity"] = refTarget;
                break;
            default:
                break;
        }

        // `unique` is offered only where a unique index is meaningful on both engines; a json or text column
        // is not one of them.
        if (type is "string" or "integer" or "uuid" && Draw(Gen.Bool, pcg))
        {
            field["unique"] = true;
        }

        return field;
    }

    /// <summary>Draws one value off the shared generator, so the order of draws is the seed's meaning.</summary>
    private static T Draw<T>(Gen<T> gen, PCG pcg) => gen.Generate(pcg, null, out _);
}
