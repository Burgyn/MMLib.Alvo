using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the CHECK constraint suite — the engine that adds a constraint in place with
/// <c>ALTER TABLE … ADD CONSTRAINT</c>, inside the apply's one transaction.
/// </summary>
public sealed class PostgreSqlCheckConstraintTests : AlvoDataCheckConstraintTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    protected override Task<Exception?> ExecuteOutsideThePortAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(
            _host!.Services.GetRequiredService<AlvoDataContextFactory>(), sql, TestContext.Current.CancellationToken);

    protected override Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired) =>
        Migrator.PlanAsync(current, desired, new MigrationOptions(), TestContext.Current.CancellationToken);

    protected override async Task ApplyAsync(SchemaModel current, SchemaModel desired, MigrationOptions options)
    {
        (await ApplyAsync(await PlanAsync(current, desired), options)).Applied.ShouldBeTrue();
        _host!.RePrime(desired);
    }

    protected override Task ExecuteAsync(MigrationPlan plan) => ApplyAsync(plan, new MigrationOptions());

    protected override async Task ApplyThroughTheRuntimeWriterAsync(SchemaModel current, SchemaModel desired)
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = await PlanAsync(current, desired);
        var candidate = new DescriptorVersion(desired, "{}", Revision: 0, DateTimeOffset.UnixEpoch);
        await _host!.Services.GetRequiredService<IRuntimeSchemaWriter>()
            .ApplyAndAppendAsync("check-constraints", plan, candidate, expectedRevision: 0, new MigrationOptions(), ct);
    }

    private Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options) =>
        Migrator.ApplyAsync(plan, options, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private ISchemaMigrator Migrator => _host!.Services.GetRequiredService<ISchemaMigrator>();
}
