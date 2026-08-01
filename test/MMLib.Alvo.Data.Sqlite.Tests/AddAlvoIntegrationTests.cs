using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// End-to-end: <c>AddAlvo</c> wired exclusively through its public surface (<c>UseSqlite</c>,
/// <c>FromDescriptor</c>) against the real <c>examples/simple-tasks/tasks.alvo.json</c> descriptor,
/// plus the fail-fast startup check when no provider is selected.
/// </summary>
public sealed class AddAlvoIntegrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-addalvo-tests-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        // Best-effort: the test method disposes its ServiceProvider (via `using var sp`) before
        // returning, which disposes the migrator/introspector and releases their (pooling-disabled)
        // connection — that is what actually releases the OS file handle, well before this method
        // runs. This is still a temp file either way, so a stray lock should not fail the test — the
        // OS reclaims temp files regardless.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddAlvo_UseSqlite_FromDescriptor_migrates_the_real_tasks_descriptor()
    {
        var descriptorPath = DescriptorPath();

        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_databasePath}").FromDescriptor(descriptorPath));

        using var sp = services.BuildServiceProvider();
        var runner = sp.GetRequiredService<SchemaMigrationRunner>();

        var result = await runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        var entityNames = introspected.Entities.Select(entity => entity.Name).ToList();

        entityNames.ShouldContain("tasks");
        entityNames.ShouldContain("projects");

        // The managed columns the mapper injects must survive end-to-end (descriptor → mapper →
        // migration → physical table) with the right nullability, not just the entity existing.
        // Both "tasks" and "projects" declare audit (created_/updated_ columns); neither declares
        // softDelete, which the mapper refuses until soft delete is implemented.
        var tasks = introspected.Entities.Single(entity => entity.Name == "tasks");
        var taskFields = tasks.Fields.Select(field => field.Name).ToList();
        taskFields.ShouldContain("created_at");
        taskFields.ShouldContain("updated_at");
        taskFields.ShouldContain("created_by");
        taskFields.ShouldContain("updated_by");
        tasks.Fields.Single(field => field.Name == "created_at").Nullable.ShouldBeFalse();
        tasks.Fields.Single(field => field.Name == "created_by").Nullable.ShouldBeTrue();

        var projects = introspected.Entities.Single(entity => entity.Name == "projects");
        projects.Fields.Select(field => field.Name).ShouldContain("updated_at");
        projects.Fields.Select(field => field.Name).ShouldNotContain("deleted_at");
    }

    /// <summary>
    /// Regression guard for Task 11 Finding 2: the showcase descriptor's rules must stay compilable
    /// end to end (descriptor → mapper → migration → policy catalog priming), not merely
    /// schema-valid. Both this descriptor and <c>simple-tasks</c> apply as they stand — the features the
    /// build does not honour (<c>computed</c>, <c>rollup</c>, <c>validation</c>, <c>default</c>,
    /// <c>softDelete</c>, <c>hooks</c>) were removed from every runnable example when the apply-time
    /// refusals landed; <c>complex-crm</c> keeps them and is marked not runnable. <c>examples/README.md</c>
    /// carries the table, and <c>DescriptorToSchemaMapperTests.Every_runnable_example_maps_without_refusal</c>
    /// holds it.
    /// </summary>
    [Fact]
    public async Task AddAlvo_UseSqlite_FromDescriptor_migrates_the_real_vehicle_registry_descriptor()
    {
        var descriptorPath = VehicleRegistryDescriptorPath();

        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_databasePath}").FromDescriptor(descriptorPath));

        using var sp = services.BuildServiceProvider();
        var runner = sp.GetRequiredService<SchemaMigrationRunner>();

        var result = await runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        var entityNames = introspected.Entities.Select(entity => entity.Name).ToList();

        entityNames.ShouldContain("owners");
        entityNames.ShouldContain("vehicles");
        entityNames.ShouldContain("inspections");
    }

    /// <summary>
    /// The apply seam a host in another assembly actually has. <c>SchemaMigrationRunner</c> is
    /// <see langword="internal"/> to the core, so <c>MMLib.Alvo.Host</c> cannot resolve it — this extension is
    /// the whole reason a standalone host can bring a descriptor up, and it is asserted through the physical
    /// tables it produced rather than through the result flag alone.
    /// </summary>
    [Fact]
    public async Task The_public_apply_extension_creates_the_descriptors_tables()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={_databasePath}")
            .FromDescriptor(VehicleRegistryDescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.ApplyAlvoDescriptorAsync(ct: TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue("a host that cannot apply maps no route at all");
        result.WasDryRun.ShouldBeFalse();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        introspected.Entities.Select(entity => entity.Name)
            .ShouldContain("vehicles", "the descriptor's entities must exist as real tables, not merely validate");
    }

    /// <summary>
    /// The options argument reaches the runner. Without this the parameter could be dropped and every
    /// existing fact would stay green, because they all pass the default.
    /// </summary>
    [Fact]
    public async Task The_public_apply_extension_honours_a_dry_run()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={_databasePath}")
            .FromDescriptor(VehicleRegistryDescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.ApplyAlvoDescriptorAsync(
            new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken);

        result.WasDryRun.ShouldBeTrue();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        introspected.Entities.ShouldBeEmpty("a dry run must plan and write nothing");
    }

    [Fact]
    public async Task AddAlvo_UseSqlite_options_only_migrates_the_real_tasks_descriptor()
    {
        var descriptorPath = DescriptorPath();

        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite(options => options.ConnectionString = $"Data Source={_databasePath}")
            .FromDescriptor(descriptorPath));

        using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<SchemaMigrationRunner>()
            .RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();
    }

    [Fact]
    public async Task AddAlvo_UseSqlite_parameterless_resolves_the_connection_string_from_configuration()
    {
        var configuration = ConfigurationWith("ConnectionStrings:Alvo", $"Data Source={_databasePath}");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlvo(alvo => alvo.UseSqlite().FromDescriptor(DescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<SchemaMigrationRunner>()
            .RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();
    }

    [Fact]
    public async Task AddAlvo_UseSqlite_from_configuration_with_a_custom_name_resolves_the_named_connection_string()
    {
        var configuration = ConfigurationWith("ConnectionStrings:Fleet", $"Data Source={_databasePath}");

        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite(configuration, "Fleet").FromDescriptor(DescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<SchemaMigrationRunner>()
            .RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();
    }

    [Fact]
    public void UseSqlite_parameterless_without_a_configured_connection_string_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddAlvo(alvo => alvo.UseSqlite());

        using var sp = services.BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(
            () => sp.GetRequiredService<ISchemaMigrator>());

        exception.Message.ShouldContain("No SQLite connection string was configured");
    }

    [Fact]
    public void UseSqlite_parameterless_without_IConfiguration_registered_fails_fast_with_a_helpful_message()
    {
        // No IConfiguration in the container at all: resolution must still fail fast with the
        // crafted guidance, not a raw "no service for IConfiguration" DI exception.
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite());

        using var sp = services.BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(
            () => sp.GetRequiredService<ISchemaMigrator>());

        exception.Message.ShouldContain("No SQLite connection string was configured");
    }

    [Fact]
    public void UseSqlite_without_a_connection_string_fails_fast_when_the_provider_is_built()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite(options => { }));

        using var sp = services.BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(
            () => sp.GetRequiredService<ISchemaMigrator>());

        exception.Message.ShouldContain("No SQLite connection string was configured");
    }

    [Fact]
    public void AddAlvo_without_a_provider_fails_fast_at_startup_validation()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var sp = services.BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(
            () => sp.GetRequiredService<IStartupValidator>().Validate());

        exception.Message.ShouldContain(AlvoProviderValidation.NoProviderRegisteredMessage);
    }

    /// <summary>
    /// Alvo's own descriptor-version history must never be reported as one of the user's entities: the runner
    /// falls back to introspection whenever there is no applied snapshot (a first run against an existing
    /// database, or a history an operator has lost), and a bookkeeping table that reaches that diff is planned
    /// for a <c>DROP</c> — silently, and destructively.
    /// </summary>
    [Fact]
    public Task A_framework_bookkeeping_table_is_never_planned_for_a_drop_descriptor_versions() =>
        AFrameworkTableSurvivesIntrospectionAsync("alvo_descriptor_versions");

    /// <summary>
    /// The same for the idempotency records. It is a separate fact per table rather than one over a list,
    /// because the failure mode is per table: <c>SystemSchemaInitializer.FrameworkTableNames</c> is one member
    /// precisely so a table cannot be forgotten, and a fact that iterated whatever that member returns could
    /// not notice a table being dropped from it.
    /// </summary>
    [Fact]
    public Task A_framework_bookkeeping_table_is_never_planned_for_a_drop_idempotency() =>
        AFrameworkTableSurvivesIntrospectionAsync("alvo_idempotency");

    /// <summary>
    /// Applies the real descriptor, then reproduces the runner's own no-snapshot fallback — introspect the live
    /// database and diff it against the applied schema — and requires that <paramref name="tableName"/> appears
    /// neither as an introspected entity nor in any step of the resulting plan.
    /// </summary>
    /// <param name="tableName">
    /// The framework table, spelled out rather than read from
    /// <c>SystemSchemaInitializer.FrameworkTableNames</c>: taking the name from the member under test is how a
    /// name dropped from that member stops being checked at all.
    /// </param>
    /// <remarks>
    /// <b>Measured, with a name deleted from that member:</b> the first thing that breaks is not a planned
    /// <c>DROP</c> but a hard <c>InvalidOperationException</c> out of the model build — <em>"the property 'id'
    /// cannot be added to the type 'alvo_idempotency'"</em> — because a bookkeeping table has no row key and the
    /// property-bag model requires one. And it breaks on <b>every first run</b>, not only on a re-apply:
    /// <c>SchemaMigrationRunner</c> reads the applied snapshot first, which is what creates these tables, and
    /// then falls back to introspection because that read found no revision yet. So the plan assertion below is
    /// the narrower of the two claims and the one worth stating; the throw is what a contributor would actually
    /// see. Both facts fail for whichever name is missing, so the pair is a per-table statement rather than a
    /// per-table discriminator.
    /// </remarks>
    private async Task AFrameworkTableSurvivesIntrospectionAsync(string tableName)
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_databasePath}").FromDescriptor(DescriptorPath()));

        using var sp = services.BuildServiceProvider();
        (await sp.GetRequiredService<SchemaMigrationRunner>().RunAsync(new MigrationOptions(), ct))
            .Applied.ShouldBeTrue();

        // Non-vacuity: the table has to be in the database, or "introspection does not report it" is trivially
        // true and this fact proves nothing about the exclusion.
        (await TableExistsAsync(tableName, ct)).ShouldBeTrue(
            $"'{tableName}' must exist by now, or this fact cannot fail for the reason it claims");

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>().IntrospectAsync(ct);
        introspected.Entities.Select(entity => entity.Name).ShouldNotContain(tableName);

        var desired = sp.GetRequiredService<ISchemaRegistry>().GetSchema();
        var plan = await sp.GetRequiredService<ISchemaMigrator>()
            .PlanAsync(introspected, desired, new MigrationOptions(), ct);
        plan.Steps.Select(step => step.Change.Entity).ShouldNotContain(
            tableName, "a re-apply planning a step against Alvo's own table would drop the framework's data");
    }

    /// <summary>Whether <paramref name="tableName"/> physically exists, read straight from SQLite's catalog.</summary>
    /// <param name="tableName">The table to look for.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    private async Task<bool> TableExistsAsync(string tableName, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static string DescriptorPath() =>
        Path.Combine(RepositoryRoot.Find(), "examples", "simple-tasks", "tasks.alvo.json");

    private static string VehicleRegistryDescriptorPath() =>
        Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");

    private static IConfiguration ConfigurationWith(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
}
