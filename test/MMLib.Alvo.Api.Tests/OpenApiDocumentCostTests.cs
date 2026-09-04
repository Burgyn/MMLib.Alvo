using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Rules;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// What building the OpenAPI document costs per request, measured through the two ports the transformer
/// reads (#126).
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is rebuilt on every request</b>, and <c>/openapi/v1.json</c> is reachable without a
/// credential, so whatever the transformer does per entity it does per anonymous request. As filed, for N
/// entities each with five generated endpoints, it did <c>6N</c> linear scans of the applied schema
/// (<c>EntityOf</c>, once from <c>Entities</c> and once per endpoint from <c>Enrich</c>) — O(N²) name
/// comparisons — and <c>6N</c> catalog reads, each building <b>two</b> fresh <c>HashSet</c>s for the
/// <c>hidden</c> and <c>readOnly</c> unions. The issue said 6N set allocations; it was 12N.
/// </para>
/// <para>
/// <b>Measured through the injected ports rather than by instrumenting the transformer.</b>
/// <c>AlvoDocumentTransformer</c> had exactly one call site into each — <c>schema.GetSchema()</c> inside
/// <c>EntityOf</c> and <c>policies.Current</c> inside <c>FlagsOf</c>, now both inside <c>Views</c>. So
/// counting the ports counts the two things this issue is about, exactly, with no production code aware it
/// is being measured.
/// </para>
/// <para>
/// <b>One decorator serves all three interfaces because production registers one instance for all three.</b>
/// <c>Rules/Setup.cs</c> registers <c>ISchemaRegistry</c> and <c>IRoleCatalogProvider</c> as factories
/// resolving <see cref="IPolicyCatalogProvider"/>, so decorating that one registration redirects every
/// reader while keeping the single primed holder the security core deliberately shares.
/// </para>
/// </remarks>
public sealed class OpenApiDocumentCostTests
{
    /// <summary>
    /// One request reads each source a fixed number of times, whatever the descriptor declares — not once per
    /// entity and again per endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on this world, before and after the fix: schema reads 19 → 2, catalog reads 18 → 1.</b>
    /// The descriptor declares three entities, so 18 is exactly the <c>6N</c> the issue describes, and the
    /// numbers below are the ones that were actually observed rather than the ones that were expected.
    /// </para>
    /// <para>
    /// <b>Three entities on purpose.</b> At one entity a linear scan and a dictionary lookup are
    /// indistinguishable — <c>6N</c> and a constant are both "6" — so a fact seeded with a single entity
    /// would have passed before the fix and proved nothing.
    /// </para>
    /// <para>
    /// <b>Why the schema is two and not one.</b> One read is the transformer's. The other is
    /// <c>EntityRouteCatalog.Entities</c>, which is the endpoint data source's, not the document's: ApiExplorer
    /// enumerates the route table to build the description groups this transformer walks. It is the only other
    /// <c>GetSchema()</c> call site in the core, it is one read regardless of entity count, and it is a
    /// different concern — so it is named here rather than folded into the number and forgotten.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Building_the_document_reads_each_source_a_fixed_number_of_times()
    {
        var (world, ports) = await CountedAsync();
        await using var _ = world;

        ports.Clear();
        var document = await world.OpenApiDocumentAsync();

        Entities(document).Count.ShouldBeGreaterThan(
            1, "a single-entity document cannot tell an O(N) scan from an O(1) lookup");
        ports.CurrentReads.ShouldBe(
            1, "the compiled catalog is read once and indexed, not once per entity and again per endpoint");
        ports.SchemaReads.ShouldBe(
            2,
            "the applied schema is read once by the transformer and once by EntityRouteCatalog when "
            + "ApiExplorer enumerates the route table — not once per endpoint");
    }

    /// <summary>
    /// The counterweight: the document still says everything it said. A transformer that read each port once
    /// because it stopped describing anything would satisfy the fact above.
    /// </summary>
    /// <remarks>
    /// Asserted structurally rather than against a byte baseline — <c>OpenApiDocumentTests</c> already owns
    /// the baseline, and this fact's job is to make the count above non-vacuous, not to duplicate it. Five
    /// operations per entity is the generated Data API's own shape: list, get, create, update, delete.
    /// </remarks>
    [Fact]
    public async Task Reading_each_source_once_still_describes_every_entity_and_operation()
    {
        var (world, ports) = await CountedAsync();
        await using var _ = world;

        ports.Clear();
        var document = await world.OpenApiDocumentAsync();
        var entities = Entities(document);

        foreach (var entity in entities)
        {
            document["components"]!["schemas"]!.AsObject()
                .ShouldContainKey(entity, $"'{entity}' lost its schema component");
            document["tags"]!.AsArray()
                .ShouldContain(tag => (string?)tag!["name"] == entity, $"'{entity}' lost its tag");
        }

        Operations(document).ShouldBe(
            entities.Count * 6, $"six operations per entity for {entities.Count} entities");
    }

    /// <summary>Every entity the document describes, read off its tags.</summary>
    private static IReadOnlyList<string> Entities(JsonObject document) =>
        [.. document["tags"]!.AsArray().Select(tag => (string)tag!["name"]!)];

    /// <summary>How many operations the document describes across every path.</summary>
    private static int Operations(JsonObject document) =>
        document["paths"]!.AsObject().Sum(path => path.Value!.AsObject().Count);

    /// <summary>
    /// A world whose policy-catalog provider counts, decorated <b>after</b> <c>AddAlvo</c> so the real
    /// provider is what gets wrapped rather than one that was never registered.
    /// </summary>
    private static async Task<(AlvoApiWorld World, CountingPolicyCatalogProvider Ports)> CountedAsync()
    {
        var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(
                MapOpenApiDocument: true,
                ConfigureServicesAfterAlvo: services => services.Decorate<IPolicyCatalogProvider>(
                    inner => new CountingPolicyCatalogProvider(inner))));

        var ports = world.Services.GetRequiredService<IPolicyCatalogProvider>()
            .ShouldBeOfType<CountingPolicyCatalogProvider>("the decoration must have replaced the provider");

        return (world, ports);
    }
}
