using Microsoft.Extensions.Options;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Internal;

public sealed class AlvoProviderValidationTests
{
    [Fact]
    public void Validate_fails_when_no_ISchemaMigrator_is_registered()
    {
        var validation = new AlvoProviderValidation(new FakeServiceProvider(schemaMigrator: null));

        var result = validation.Validate(null, new AlvoOptions());

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldBe(AlvoProviderValidation.NoProviderRegisteredMessage);
    }

    [Fact]
    public void Validate_succeeds_when_an_ISchemaMigrator_is_registered()
    {
        var validation = new AlvoProviderValidation(new FakeServiceProvider(new NoOpSchemaMigrator()));

        var result = validation.Validate(null, new AlvoOptions());

        result.Failed.ShouldBeFalse();
        result.ShouldBe(ValidateOptionsResult.Success);
    }

    private sealed class FakeServiceProvider(ISchemaMigrator? schemaMigrator) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISchemaMigrator) ? schemaMigrator : null;
    }

    private sealed class NoOpSchemaMigrator : ISchemaMigrator
    {
        public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default) =>
            Task.FromResult(new MigrationPlan { Steps = [] });

        public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default) =>
            Task.FromResult(new MigrationResult(true, plan, false));
    }
}
