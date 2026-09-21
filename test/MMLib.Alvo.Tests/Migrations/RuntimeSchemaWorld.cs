using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// One assembled <see cref="RuntimeSchemaService"/> and the collaborators a fact needs to look at, plus
/// the descriptors both suites plan against.
/// </summary>
/// <remarks>
/// <para>
/// <b>One fixture, two suites.</b> <c>RuntimeSchemaServiceTests</c> and
/// <c>RuntimeSchemaServicePreviewTests</c> plan against the same descriptors, and a second copy of
/// "one field, two fields, three fields" is how two suites come to disagree about what a destructive
/// diff is.
/// </para>
/// <para>
/// <b>The writer and the catalog provider are counted, not replaced.</b> Both decorators forward to the
/// real implementation, so nothing here changes what the service does — they exist because
/// "a preview wrote nothing" and "a preview primed nothing" are claims about calls that did
/// <em>not</em> happen, which no assertion over the store's contents can make on its own.
/// </para>
/// </remarks>
internal sealed class RuntimeSchemaWorld
{
    /// <summary>One entity with one field — the smallest descriptor that applies.</summary>
    internal const string OneField = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    /// <summary>
    /// <see cref="OneField"/> plus an optional field — an <c>AddField</c> step, never destructive, so a plan
    /// against <see cref="OneField"/> is non-empty without tripping the destructive guardrail.
    /// </summary>
    internal const string TwoFields = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "notes": { "type": "string" }
              }
            }
          }
        }
        """;

    /// <summary>
    /// <see cref="TwoFields"/> plus a third optional field, so a plan <em>forward</em> from
    /// <see cref="TwoFields"/> is a real step and planning <em>backward</em> to it is a drop.
    /// </summary>
    internal const string ThreeFields = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "notes": { "type": "string" },
                "priority": { "type": "string" }
              }
            }
          }
        }
        """;

    /// <summary>
    /// <see cref="OneField"/> plus a <em>required</em> field. Adding it is still non-destructive
    /// (<c>AddField</c>), but rolling back from it to <see cref="OneField"/> drops that field, which is.
    /// </summary>
    internal const string OneFieldPlusRequired = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "assignee": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    /// <summary>The project every fact in both suites applies to.</summary>
    internal const string Project = "demo";

    private RuntimeSchemaWorld(
        InMemoryDescriptorVersionStore versions,
        CountingRuntimeSchemaWriter writer,
        CountingPolicyCatalogs policyCatalogs)
    {
        Versions = versions;
        Writer = writer;
        PolicyCatalogs = policyCatalogs;
        Service = new RuntimeSchemaService(
            new DescriptorValidator(), new InMemorySchemaMigrator(), versions, writer,
            new CelCompiler(), policyCatalogs);
    }

    /// <summary>The service under test.</summary>
    internal RuntimeSchemaService Service { get; }

    /// <summary>The append-only history the service reads and the writer appends to.</summary>
    internal InMemoryDescriptorVersionStore Versions { get; }

    /// <summary>The atomic apply seam, counted.</summary>
    internal CountingRuntimeSchemaWriter Writer { get; }

    /// <summary>The policy-catalog holder, counted.</summary>
    internal CountingPolicyCatalogs PolicyCatalogs { get; }

    /// <summary>A world with nothing applied yet.</summary>
    internal static RuntimeSchemaWorld Empty() => Around(new InMemoryDescriptorVersionStore());

    /// <summary>A world with nothing applied yet, over a store the caller also holds.</summary>
    /// <remarks>
    /// <b>The same store instance reaches the writer and the service.</b> The writer delegates its append
    /// there and the service reads its history from there, so two instances would make every append
    /// invisible to the very service that has to see it.
    /// </remarks>
    /// <param name="versions">The history store to build the world around.</param>
    internal static RuntimeSchemaWorld Around(InMemoryDescriptorVersionStore versions) => new(
        versions,
        new CountingRuntimeSchemaWriter(new InMemoryRuntimeSchemaWriter(versions)),
        new CountingPolicyCatalogs(new PolicyCatalogProvider()));

    /// <summary>A world with <paramref name="descriptorJson"/> already applied as revision 1.</summary>
    /// <remarks>
    /// <b>The counters are forgotten afterwards</b>, so a fact measures its own call rather than its call
    /// plus the setup that made the world interesting.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<RuntimeSchemaWorld> WithAppliedAsync(string descriptorJson, CancellationToken ct)
    {
        var world = Empty();
        await world.Service.ApplyAsync(Project, descriptorJson, expectedRevision: 0, new MigrationOptions(), ct);
        world.Writer.Forget();
        world.PolicyCatalogs.Forget();

        return world;
    }
}

/// <summary>Counts atomic applies, forwarding every one of them to the real writer.</summary>
/// <param name="inner">The writer that actually applies and appends.</param>
internal sealed class CountingRuntimeSchemaWriter(IRuntimeSchemaWriter inner) : IRuntimeSchemaWriter
{
    private int _applies;

    /// <summary>How many times <see cref="ApplyAndAppendAsync"/> has been called.</summary>
    internal int Applies => Volatile.Read(ref _applies);

    /// <summary>Forgets the count, so one fact can measure one call.</summary>
    internal void Forget() => Volatile.Write(ref _applies, 0);

    /// <inheritdoc/>
    public Task<DescriptorVersion> ApplyAndAppendAsync(
        string project, MigrationPlan plan, DescriptorVersion candidate,
        int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _applies);

        return inner.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct);
    }
}

/// <summary>
/// Counts calls to <see cref="IPolicyCatalogProvider.SetCurrent"/>, forwarding everything to the real
/// provider.
/// </summary>
/// <remarks>
/// <b>A second counter beside <c>test/_shared/api/CountingPolicyCatalogProvider.cs</c>, and it counts the
/// other direction.</b> That one counts <em>reads</em> of <see cref="IPolicyCatalogProvider.Current"/> and
/// of <see cref="ISchemaRegistry.GetSchema"/> and forwards <see cref="IPolicyCatalogProvider.SetCurrent"/>
/// uncounted; it is also linked only into the API test projects. What a preview fact needs is the write —
/// "nothing primed" — which that type cannot answer.
/// </remarks>
/// <param name="inner">The provider that actually holds the catalog.</param>
internal sealed class CountingPolicyCatalogs(IPolicyCatalogProvider inner) : IPolicyCatalogProvider
{
    private int _setCurrentCalls;

    /// <summary>How many times <see cref="SetCurrent"/> has been called.</summary>
    internal int SetCurrentCalls => Volatile.Read(ref _setCurrentCalls);

    /// <summary>Forgets the count, so one fact can measure one call.</summary>
    internal void Forget() => Volatile.Write(ref _setCurrentCalls, 0);

    /// <inheritdoc/>
    public PolicyCatalog? Current => inner.Current;

    /// <inheritdoc/>
    public RoleCatalog? DeclaredRoles => inner.DeclaredRoles;

    /// <inheritdoc/>
    public SchemaModel GetSchema() => inner.GetSchema();

    /// <inheritdoc/>
    public void SetCurrent(string project, PolicyCatalog catalog)
    {
        Interlocked.Increment(ref _setCurrentCalls);
        inner.SetCurrent(project, catalog);
    }
}
