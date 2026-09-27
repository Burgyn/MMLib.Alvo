using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The keys <b>inside</b> a block this build honours that it does not honour themselves —
/// <c>entity.storage: dynamic</c>, a non-local <c>auth.providers</c>, and <c>entity.realtime</c> — asserted on
/// which slot the one warning names, in both directions (docs/todo-admin.md §8d items 21 and 27).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sibling of <see cref="UnhonouredSubsystemsTests"/> rather than more rows there.</b> That file pins
/// <see cref="UnhonouredSubsystems.All"/> to the schema's <em>top-level</em> blocks and to their order; a slot
/// such as <c>auth.providers</c> sits under <c>auth</c>, which <c>CapabilityReport.Honoured</c> lists, so it
/// cannot be a row of the top-level table without breaking the fact that keeps honoured and warned disjoint.
/// </para>
/// <para>
/// <b>Every descriptor here is parsed, never applied</b>, as in the sibling file: these facts prove the warning
/// is correct, and the boot's own facts prove it is reached — <see cref="UnhonouredSubsystems.Warn"/> reads
/// <see cref="UnhonouredSubsystems.DeclaredBy"/>, which is what these rows extend.
/// </para>
/// </remarks>
public class UnhonouredKeysTests
{
    private const string StorageSlot = "entity.storage";
    private const string ProvidersSlot = "auth.providers";
    private const string RealtimeSlot = "entity.realtime";

    /// <summary>
    /// <b>An entity with <c>storage: dynamic</c> is warned about although <c>dynamicEntities</c> is not
    /// enabled</b> — the silent case §8d item 21 found: the mapper drops the entity and nothing said so.
    /// </summary>
    [Fact]
    public void A_dynamic_entity_is_warned_about_without_dynamic_entities_enabled()
    {
        var warning = WarningFor("""{ "notes": { "storage": "dynamic", "fields": { "title": { "type": "string" } } } }""");

        warning.ShouldNotBeNull("the entity is dropped by the mapper, and an author who is not told debugs the API");
        warning.ShouldContain(StorageSlot, Shouldly.Case.Sensitive);
        warning.ShouldNotContain("dynamicEntities", Shouldly.Case.Sensitive, "the block itself was not declared");
    }

    /// <summary>A physical entity — declared so, or by default — earns no storage line.</summary>
    [Fact]
    public void A_physical_entity_earns_no_storage_warning()
        => WarningFor("""{ "notes": { "storage": "physical", "fields": { "title": { "type": "string" } } } }""")
            .ShouldBeNull("a warning every physical entity earns is one every operator learns to filter out");

    /// <summary>
    /// <b>A provider other than <c>local</c> is warned about</b>, because only local credentials exist in this
    /// build and <c>auth</c> is on the honoured list for its roles alone.
    /// </summary>
    [Fact]
    public void A_provider_other_than_local_is_warned_about()
    {
        var warning = WarningFor(Entities, """{ "providers": ["local", "google"] }""");

        warning.ShouldNotBeNull();
        warning.ShouldContain(ProvidersSlot, Shouldly.Case.Sensitive);
    }

    /// <summary><c>local</c> alone is exactly what this build does, so it earns no line.</summary>
    [Fact]
    public void Local_alone_earns_no_provider_warning()
        => WarningFor(Entities, """{ "providers": ["local"] }""").ShouldBeNull();

    /// <summary>
    /// <b><c>entity.realtime</c> is reported and never warned</b>: its default is <c>true</c>, so a line at apply
    /// would fire on every descriptor, and the capability report is where it is said instead.
    /// </summary>
    [Fact]
    public void Realtime_is_reported_but_never_warned_at_apply()
    {
        WarningFor("""{ "notes": { "realtime": true, "fields": { "title": { "type": "string" } } } }""")
            .ShouldBeNull("the one line every descriptor earns is the line nobody reads");

        UnhonouredSubsystems.ReportedOnly.Select(entry => entry.Block).ShouldContain(RealtimeSlot);
        UnhonouredSubsystems.DeclaredBy(Parse(Entities, auth: null))
            .Select(entry => entry.Block)
            .ShouldNotContain(RealtimeSlot, "DeclaredBy is what the apply warns about");
    }

    /// <summary>
    /// The showcase declares <c>google</c> beside <c>local</c>, so its one warning now names the provider slot as
    /// well as its five top-level blocks.
    /// </summary>
    [Fact]
    public void The_showcase_warning_names_its_declared_provider()
    {
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json")));
        var logger = new CapturingLogger();

        UnhonouredSubsystems.Warn(logger, descriptor);

        logger.Warnings.ShouldHaveSingleItem().ShouldContain(ProvidersSlot, Shouldly.Case.Sensitive);
    }

    /// <summary>
    /// <b>Every slot names a key the frozen schema declares</b>, under the block its first segment names —
    /// <c>entity.*</c> under <c>$defs/entity</c>, anything else under the root's own property.
    /// </summary>
    /// <remarks>
    /// The sibling file's reason, one level down: a slot matching no key warns about nothing forever and reads as
    /// coverage.
    /// </remarks>
    [Fact]
    public void Every_slot_names_a_key_the_schema_declares()
    {
        var schema = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;

        foreach (var slot in UnhonouredSubsystems.WithinBlocks.Concat(UnhonouredSubsystems.ReportedOnly))
        {
            var (block, key) = Split(slot.Block);
            var owner = block == "entity" ? schema["$defs"]!["entity"] : schema["properties"]![block];

            owner.ShouldNotBeNull($"'{slot.Block}' names no block of the schema");
            owner!["properties"]!.AsObject().ContainsKey(key).ShouldBeTrue($"'{slot.Block}' names no declared key");
        }
    }

    /// <summary>
    /// No slot sits under a block the top-level table already warns about: the top-level line would already name
    /// it, and two lines about one block are one too many.
    /// </summary>
    [Fact]
    public void No_slot_sits_under_a_block_already_warned_about()
    {
        var blocks = UnhonouredSubsystems.All.Select(entry => entry.Block).ToHashSet(StringComparer.Ordinal);

        UnhonouredSubsystems.WithinBlocks.Concat(UnhonouredSubsystems.ReportedOnly)
            .ShouldAllBe(slot => !blocks.Contains(Split(slot.Block).Block));
    }

    private const string Entities = """{ "notes": { "fields": { "title": { "type": "string" } } } }""";

    /// <summary>The one warning a descriptor with these entities earns, or null when it earns none.</summary>
    private static string? WarningFor(string entities, string? auth = null)
    {
        var logger = new CapturingLogger();
        UnhonouredSubsystems.Warn(logger, Parse(entities, auth));
        return logger.Warnings.SingleOrDefault();
    }

    private static AlvoDescriptor Parse(string entities, string? auth)
        => AlvoDescriptor.Parse(auth is null
            ? $$"""{ "apiVersion": "alvo.dev/v1", "name": "keys", "entities": {{entities}} }"""
            : $$"""{ "apiVersion": "alvo.dev/v1", "name": "keys", "auth": {{auth}}, "entities": {{entities}} }""");

    private static (string Block, string Key) Split(string slot)
    {
        var dot = slot.IndexOf('.', StringComparison.Ordinal);
        dot.ShouldBeGreaterThan(0, $"'{slot}' is not a qualified slot");
        return (slot[..dot], slot[(dot + 1)..]);
    }
}
