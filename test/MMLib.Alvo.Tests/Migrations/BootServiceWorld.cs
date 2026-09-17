using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// One <see cref="AlvoBootService"/> over DB-less ports, driven through the lifecycle method a host calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>No host, and that is the point.</b> <c>AlvoBootService</c> is an <c>IHostedLifecycleService</c>, so
/// <c>StartingAsync</c> is a method with arguments like any other — every boot decision it makes is reachable
/// by constructing it with its ten ports and calling that method. Booting a web host to reach the same
/// decisions costs a server, a socket and a SQLite file for facts that are about none of those things; the
/// end-to-end guarantees that <em>are</em> about them (the server is not listening yet, the process exits)
/// stay in <c>MMLib.Alvo.Host.Tests</c>, which is where they can be observed at all.
/// </para>
/// <para>
/// Every port is a fake this class owns, so a fact states the database's answer instead of arranging a
/// database that would give it: the applied snapshot, the descriptor history and the live schema are three
/// independent knobs, which is what lets <see cref="AlvoBootService"/>'s own fallbacks be told apart.
/// </para>
/// </remarks>
internal sealed class BootServiceWorld : IDisposable
{
    /// <summary>The project every descriptor below declares, and the key they are stored under.</summary>
    internal const string Project = "depots";

    /// <summary>The baseline descriptor: one entity, one optional field.</summary>
    internal const string City = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": { "fields": { "city": { "type": "string" } } }
          }
        }
        """;

    /// <summary>
    /// <see cref="City"/> plus one optional field — an <c>AddField</c> plan, which is non-empty and
    /// destructive in neither direction, so it is drift a mode can be asked to apply or to refuse.
    /// </summary>
    internal const string CityAndTown = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": { "fields": { "city": { "type": "string" }, "town": { "type": "string" } } }
          }
        }
        """;

    /// <summary>A valid descriptor that declares a different project than the key it is stored under.</summary>
    internal const string NamedWarehouses = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "warehouses",
          "entities": {
            "depots": { "fields": { "city": { "type": "string" } } }
          }
        }
        """;

    /// <summary>A descriptor this build refuses: <c>entities</c> is required and is not there.</summary>
    internal const string Unservable = """
        { "apiVersion": "alvo.dev/v1", "name": "depots" }
        """;

    /// <summary>
    /// A descriptor the <b>schema validator accepts and the policy catalog refuses</b>: its authorization
    /// rule reads a field the entity does not declare, so the CEL compiler has nothing to bind
    /// <c>owner_id</c> to.
    /// </summary>
    /// <remarks>
    /// The other unservable fixture fails on a missing <c>entities</c> block, which is the JSON-Schema arm.
    /// This one exists because the boot path's refusal catches <b>two</b> exceptions from two different
    /// passes, and the second is the one that matters for a dashboard-first host: the stored descriptor is a
    /// DATABASE ROW, so a rule that does not compile is a row somebody wrote, and the rule in question is an
    /// authorization rule. Without this fixture the arm that re-validates CEL on load is unexercised, and an
    /// arm nothing reaches is an arm that can be deleted without a test noticing.
    /// </remarks>
    internal const string UnservableRule = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": {
              "fields": { "city": { "type": "string" } },
              "rules": { "list": "owner_id == @user.id" }
            }
          }
        }
        """;

    private readonly InMemoryDescriptorVersionStore _appends = new();
    private readonly CountingVersionStore _history;
    private readonly StubAppliedSchemaStore _store = new();
    private readonly PolicyCatalogProvider _catalog = new();
    private readonly CapturingLogger _captured = new();
    private readonly ILoggerFactory _loggers;

    /// <summary>Initializes a new instance of the <see cref="BootServiceWorld"/> class.</summary>
    internal BootServiceWorld()
    {
        _history = new CountingVersionStore(_appends);
        _loggers = LoggerFactory.Create(logging => logging.AddProvider(_captured));
    }

    /// <summary>The descriptor source a code-first host attaches, or <see langword="null"/> for dashboard-first.</summary>
    internal IDescriptorSource? Source { get; set; }

    /// <summary>What the introspector reports — read only when no applied snapshot was recorded.</summary>
    internal SchemaModel LiveSchema { get; set; } = new([]);

    /// <summary>What the introspector throws instead of answering, when a fact asked it to.</summary>
    internal Exception? Introspection { get; set; }

    /// <summary>How many optimistic-lock losses the writer reports before the append is allowed to land.</summary>
    internal int ConflictsBeforeTheWriteLands { get; set; }

    /// <summary>The configured startup mode, project and destructive allowance.</summary>
    internal AlvoSchemaOptions Options { get; } = new();

    /// <summary>What the boot published for a readiness probe to read.</summary>
    internal AlvoBootState State { get; } = new();

    /// <summary>Every line the boot logged, level included.</summary>
    internal IReadOnlyList<CapturedLogEntry> Log => _captured.Entries;

    /// <summary>How many times anything read the whole descriptor history.</summary>
    internal int HistoryReads => _history.ListCalls;

    /// <summary>Every entity the primed catalog reports — empty when nothing primed.</summary>
    internal IReadOnlyList<string> PrimedEntities =>
        [.. _catalog.GetSchema().Entities.Select(entity => entity.Name)];

    /// <summary>Attaches a code-first descriptor source over <paramref name="descriptorJson"/>.</summary>
    /// <param name="descriptorJson">What the source hands stage 0.</param>
    internal BootServiceWorld BootingFrom(string descriptorJson)
    {
        Source = new StubDescriptorSource(descriptorJson);

        return this;
    }

    /// <summary>Attaches a descriptor source that refuses instead of answering.</summary>
    /// <param name="refusal">What the source throws when stage 0 reads it.</param>
    internal BootServiceWorld BootRefusedBy(Exception refusal)
    {
        Source = new RefusingDescriptorSource(refusal);

        return this;
    }

    /// <summary>
    /// Records <paramref name="descriptorJson"/> as the project's applied history, in order, and makes the
    /// last of them the applied snapshot the store reports.
    /// </summary>
    /// <param name="descriptorJson">The descriptors this database has had applied to it, oldest first.</param>
    internal BootServiceWorld Applied(params string[] descriptorJson)
    {
        ArgumentNullException.ThrowIfNull(descriptorJson);

        for (var index = 0; index < descriptorJson.Length; index++)
        {
            var schema = SchemaOf(descriptorJson[index]);
            _appends.AppendAsync(
                Project,
                new DescriptorVersion(schema, descriptorJson[index], Revision: 0, DateTimeOffset.UtcNow),
                index,
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();

            _store.Current = new AppliedSchema(schema, descriptorJson[index], index + 1, DateTimeOffset.UtcNow);
        }

        return this;
    }

    /// <summary>Records one history row verbatim, whether or not this build can read it back.</summary>
    /// <param name="descriptorJson">The JSON to store under the project key.</param>
    internal BootServiceWorld StoredVerbatim(string descriptorJson)
    {
        _appends.AppendAsync(
            Project,
            new DescriptorVersion(new SchemaModel([]), descriptorJson, Revision: 0, DateTimeOffset.UtcNow),
            expectedRevision: 0,
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        return this;
    }

    /// <summary>The revisions the descriptor history holds — what the store says, not what a boot published.</summary>
    internal IReadOnlyList<int> RecordedRevisions() =>
        [.. _appends.ListAsync(Project, TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult().Select(version => version.Revision)];

    /// <summary>Builds the service under test over this world's ports.</summary>
    internal AlvoBootService Build() =>
        new(
            Plan(),
            new InMemorySchemaMigrator(),
            Writer(),
            new StubSchemaIntrospector(this),
            _store,
            _history,
            _catalog,
            State,
            new OptionsWrapper<AlvoSchemaOptions>(Options),
            _loggers.CreateLogger<AlvoBootService>());

    /// <summary>
    /// The same ten ports <see cref="Build"/> passes, positionally — for the facts that null one of them.
    /// </summary>
    internal object?[] Ports() =>
    [
        Plan(),
        new InMemorySchemaMigrator(),
        Writer(),
        new StubSchemaIntrospector(this),
        _store,
        _history,
        _catalog,
        State,
        new OptionsWrapper<AlvoSchemaOptions>(Options),
        _loggers.CreateLogger<AlvoBootService>(),
    ];

    private DescriptorBootPlan Plan() =>
        new(Source, new DescriptorValidator(), new CelCompiler(), _loggers.CreateLogger<DescriptorBootPlan>());

    private ConflictingRuntimeSchemaWriter Writer() =>
        new ConflictingRuntimeSchemaWriter(
            new InMemoryRuntimeSchemaWriter(_appends), ConflictsBeforeTheWriteLands);

    /// <summary>Runs the boot the way a host does, and hands back what it threw, if anything.</summary>
    internal async Task<Exception?> BootAsync()
    {
        try
        {
            await Build().StartingAsync(TestContext.Current.CancellationToken);

            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    /// <summary>Runs the boot and fails the fact if it refused, so a happy-path fact reads as one.</summary>
    internal async Task BootOrFailAsync()
    {
        var failure = await BootAsync();

        failure.ShouldBeNull();
    }

    /// <summary>The schema a descriptor maps to — what an applied snapshot of it would hold.</summary>
    /// <param name="descriptorJson">The descriptor to map.</param>
    internal static SchemaModel SchemaOf(string descriptorJson) =>
        DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));

    /// <inheritdoc/>
    public void Dispose()
    {
        _loggers.Dispose();
        _captured.Dispose();
    }

    /// <summary>A descriptor source over a string the fact already has.</summary>
    /// <param name="descriptorJson">What every load returns.</param>
    private sealed class StubDescriptorSource(string descriptorJson) : IDescriptorSource
    {
        public Task<string> LoadAsync(CancellationToken ct = default) => Task.FromResult(descriptorJson);
    }

    /// <summary>A descriptor source that refuses, the way a host's own source may.</summary>
    /// <param name="refusal">What every load throws.</param>
    private sealed class RefusingDescriptorSource(Exception refusal) : IDescriptorSource
    {
        public Task<string> LoadAsync(CancellationToken ct = default) => Task.FromException<string>(refusal);
    }

    /// <summary>The applied snapshot the store reports, as a settable field.</summary>
    private sealed class StubAppliedSchemaStore : IAppliedSchemaStore
    {
        internal AppliedSchema? Current { get; set; }

        public Task<AppliedSchema?> GetCurrentAsync(string project, CancellationToken ct = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(string project, AppliedSchema snapshot, CancellationToken ct = default)
        {
            Current = snapshot;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The live schema, read through the world so a fact can change it after the service was built.
    /// </summary>
    /// <param name="world">The world holding the answer.</param>
    private sealed class StubSchemaIntrospector(BootServiceWorld world) : ISchemaIntrospector
    {
        public Task<SchemaModel> IntrospectAsync(CancellationToken ct = default) =>
            world.Introspection is { } failure ? Task.FromException<SchemaModel>(failure) : Task.FromResult(world.LiveSchema);
    }

    /// <summary>The history store, plus a count of the O(N) reads the ordering gate is allowed to make.</summary>
    /// <param name="inner">The store that actually holds the rows.</param>
    private sealed class CountingVersionStore(IDescriptorVersionStore inner) : IDescriptorVersionStore
    {
        internal int ListCalls { get; private set; }

        public Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default) =>
            inner.GetCurrentAsync(project, ct);

        public Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default) =>
            inner.GetAsync(project, revision, ct);

        public Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default)
        {
            ListCalls++;

            return inner.ListAsync(project, ct);
        }

        public Task<DescriptorVersion> AppendAsync(
            string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default) =>
            inner.AppendAsync(project, candidate, expectedRevision, ct);
    }

    /// <summary>
    /// The writer, reporting the optimistic-lock loss a replica that got there second sees — the first
    /// <paramref name="conflicts"/> attempts only.
    /// </summary>
    /// <param name="inner">The writer the surviving attempt delegates to.</param>
    /// <param name="conflicts">How many attempts lose the race before one is allowed to win.</param>
    private sealed class ConflictingRuntimeSchemaWriter(IRuntimeSchemaWriter inner, int conflicts)
        : IRuntimeSchemaWriter
    {
        private int _attempts;

        public Task<DescriptorVersion> ApplyAndAppendAsync(
            string project,
            MigrationPlan plan,
            DescriptorVersion candidate,
            int expectedRevision,
            MigrationOptions options,
            CancellationToken ct = default)
        {
            _attempts++;

            return _attempts <= conflicts
                ? Task.FromException<DescriptorVersion>(new DescriptorConcurrencyException(project, expectedRevision, expectedRevision + 1))
                : inner.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct);
        }
    }
}
