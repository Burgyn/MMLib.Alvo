using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

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
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddAlvo_UseSqlite_FromDescriptor_migrates_the_real_tasks_descriptor()
    {
        var descriptorPath = Path.Combine(RepositoryRoot.Find(), "examples", "simple-tasks", "tasks.alvo.json");

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
}
