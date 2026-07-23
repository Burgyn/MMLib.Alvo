using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class InMemorySchemaMigratorTests : SchemaMigratorContractTests
{
    private readonly InMemorySchemaMigrator _migrator = new();

    protected override ISchemaMigrator CreateMigrator() => _migrator;

    protected override Task<SchemaModel> IntrospectAsync() => Task.FromResult(_migrator.Applied);
}
