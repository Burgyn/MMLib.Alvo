using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class InMemoryRuntimeSchemaWriterTests : RuntimeSchemaWriterContractTests
{
    protected override IRuntimeSchemaWriter CreateWriter() =>
        new InMemoryRuntimeSchemaWriter(new InMemoryDescriptorVersionStore());
}
