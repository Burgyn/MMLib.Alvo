using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// An applied schema that never passed descriptor validation, declaring a field the query string reserves —
/// the substituted-registry shape the route-materialisation belt exists for.
/// </summary>
/// <remarks>
/// Shared by the two suites that need a schema Alvo refuses to route: <c>AlvoHealthTests</c>, which asserts
/// what the refusal does to the two probes, and <c>DataApiConventionTests</c>, which asserts that a host's
/// conventions are still sealed on that path. A second copy is how the two would come to be refused for
/// different reasons.
/// </remarks>
internal sealed class RegistryShadowingAReservedKey : ISchemaRegistry
{
    private readonly SchemaModel _schema = new([
        new EntitySchema
        {
            Name = "widgets",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = ReservedQueryKeys.Limit, Type = FieldType.Integer },
            ],
        },
    ]);

    /// <inheritdoc/>
    public SchemaModel GetSchema() => _schema;
}
