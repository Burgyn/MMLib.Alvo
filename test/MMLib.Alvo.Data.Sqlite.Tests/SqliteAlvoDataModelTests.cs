using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataModelTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// The de-risking spike's own first-named open risk: EF caches exactly one model per
    /// <c>DbContext</c> CLR type, so without a schema-keyed cache key the first descriptor a process
    /// ever applied would be served forever — a field added by a runtime apply invisible, a removed one
    /// still queried.
    /// </summary>
    [Fact]
    public async Task Re_applying_a_descriptor_with_a_new_field_invalidates_the_cached_model()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        using (var before = factory.Create())
        {
            Properties(before, "vehicle").ShouldNotContain("colour");
        }

        host.RePrime(SchemaWith("plate", "colour"));

        using var after = factory.Create();
        Properties(after, "vehicle").ShouldContain("colour");
    }

    /// <summary>
    /// The other half of the model-cache contract, and the one an always-new token satisfies vacuously: the
    /// token must <b>not</b> change while the applied schema has not. Model building is the most expensive
    /// thing EF does at runtime, so a token minted per call would rebuild the whole model on every single
    /// data operation — invisible to every other fact in this file, and fatal to the p95 criterion PR3
    /// inherits.
    /// </summary>
    /// <remarks>
    /// The token and the cache key are what Alvo owns, and they are what this asserts. It deliberately does
    /// <b>not</b> assert that two contexts sharing a token get the same <c>IModel</c> instance: that is a
    /// property of EFs own model cache — an <c>IMemoryCache</c> with a size limit — so a pass would be
    /// evidence the cache retained the entry rather than evidence Alvo behaved, and a failure evidence of
    /// eviction rather than of a defect. A suite that builds a fresh applied schema per database evicts often
    /// enough to make that assertion intermittently red, which is worse than not making it.
    /// </remarks>
    [Fact]
    public async Task An_unchanged_schema_keeps_its_model_cache_key()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();

        using var first = factory.Create();
        using var second = factory.Create();

        second.ModelToken.ShouldBe(first.ModelToken);
        CacheKey(second).ShouldBe(CacheKey(first));
    }

    /// <summary>
    /// The mechanism by which "built once per applied schema" actually happens, asserted on the pure function
    /// that decides it rather than on whether EF happened to keep the entry: a re-prime mints a new token, so
    /// the cache key differs and EF has to build again.
    /// </summary>
    [Fact]
    public async Task A_re_primed_schema_gets_a_different_model_cache_key()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        using var before = factory.Create();

        host.RePrime(SchemaWith("plate", "colour"));
        using var after = factory.Create();

        after.ModelToken.ShouldNotBe(before.ModelToken);
        CacheKey(after).ShouldNotBe(CacheKey(before));
    }

    private static object CacheKey(AlvoDataContext context) =>
        new AlvoModelCacheKeyFactory().Create(context, designTime: false);

    /// <summary>
    /// Spike <c>Q4g</c>: an all-optional read model is the only shape in which a <c>hidden</c>
    /// <c>NOT NULL</c> column can be replaced by a projected typed SQL <c>NULL</c>. The schema-faithful
    /// model throws instead — and throws a <em>different</em> exception type on each engine, which
    /// principle 3 forbids.
    /// </summary>
    [Fact]
    public async Task Every_read_model_property_is_optional_even_for_a_not_null_column()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        var plate = context.Model.FindEntityType("vehicle")!.FindProperty("plate")!;
        plate.IsNullable.ShouldBeTrue();
    }

    /// <summary>
    /// The physical column is still <c>NOT NULL</c>, so relaxing required-ness in the read model
    /// weakens nothing on the write path (spike <c>Q4h</c>).
    /// </summary>
    [Fact]
    public async Task The_column_behind_an_optional_property_is_still_not_null()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        var written = await NullPlateInsertAsync(context);

        written.ShouldContain("NOT NULL constraint failed: vehicle.plate");
    }

    /// <summary>
    /// The key is the one property that does not stay optional: EF re-marks a key property required when
    /// <c>HasKey</c> is applied, whatever the field configuration asked for. Pinned rather than left
    /// implicit, because it means the id column is the one field a <c>hidden</c> rule could not
    /// NULL-project — the shaper would throw on it — so a later reader must not read the all-optional
    /// rule as being without exception.
    /// </summary>
    [Fact]
    public async Task The_key_is_the_one_property_that_does_not_stay_optional()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        context.Model.FindEntityType("vehicle")!.FindProperty("id")!.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public async Task Queries_do_not_track_so_a_returned_row_can_never_be_written_back()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
    }

    /// <summary>
    /// A dynamic entity is not in this model at all: F7 registers a dynamic dialect and renderer rather
    /// than adding a branch here, and until then such an entity must be as unreachable as an unknown one.
    /// </summary>
    [Fact]
    public async Task A_dynamic_entity_is_absent_from_the_physical_read_model()
    {
        var host = await _fixture.StartAsync(new SchemaModel([Dynamic("evidence")]));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        context.Model.FindEntityType("evidence").ShouldBeNull();
    }

    private static async Task<string> NullPlateInsertAsync(DbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"vehicle\" (\"id\", \"plate\") VALUES ('x', NULL)";

        var thrown = await Should.ThrowAsync<Exception>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        return thrown.Message;
    }

    private static IReadOnlyList<string> Properties(DbContext context, string entity) =>
        [.. context.Model.FindEntityType(entity)!.GetProperties().Select(property => property.Name)];

    private static SchemaModel SchemaWith(params string[] notNullStringFields) => new(
    [
        new EntitySchema
        {
            Name = "vehicle",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                .. notNullStringFields.Select(name =>
                    new FieldSchema { Name = name, Type = FieldType.String, Required = true, MaxLength = 32 }),
            ],
        },
    ]);

    private static EntitySchema Dynamic(string name) => new()
    {
        Name = name,
        Storage = EntityStorage.Dynamic,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
