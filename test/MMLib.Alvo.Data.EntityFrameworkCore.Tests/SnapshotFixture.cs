using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using DescriptorFieldType = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Resolves a real <see cref="PolicyDecision"/> for the canonical <c>vehicle</c> fixture, the way every
/// production consumer gets one: compile a descriptor's rules into a <see cref="PolicyCatalog"/>, prime it,
/// and ask <see cref="IPolicyEngine"/>. An allow is <see langword="internal"/> to the core on purpose, so a
/// hand-built decision is not an option — and a decision this suite composed itself would not carry the
/// synthesized tenant scope, which is half of what the composer has to prove.
/// </summary>
internal static class SnapshotFixture
{
    /// <summary>The schema every fixture rule is compiled against — the one canonical data-path entity.</summary>
    internal static SchemaModel Schema { get; } = new([AlvoDataFixtures.Vehicle]);

    /// <summary>
    /// A descriptor over the fixture entity carrying whichever rules a test needs, and marking
    /// <paramref name="hiddenFields"/> <c>hidden</c>.
    /// </summary>
    internal static AlvoDescriptor VehicleWith(
        string? list = null,
        string? get = null,
        string? create = null,
        string? update = null,
        string? delete = null,
        params string[] hiddenFields) => new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = "read-statement-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [AlvoDataFixtures.Vehicle.Name] = new EntityDescriptor
                {
                    Fields = FieldDescriptors(hiddenFields),
                    Rules = new AccessRules { List = list, Get = get, Create = create, Update = update, Delete = delete },
                },
            },
        };

    /// <summary>Resolves the decision <paramref name="operation"/> gets for the fixture caller.</summary>
    internal static PolicyDecision Decision(
        IServiceProvider services, AlvoDescriptor descriptor, DataOperation operation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        var catalog = PolicyCatalog.Build(descriptor, Schema, services.GetRequiredService<ICelCompiler>());
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);

        return services.GetRequiredService<IPolicyEngine>()
            .Resolve(AlvoDataFixtures.Vehicle.Name, operation, AlvoDataFixtures.Caller);
    }

    /// <summary>
    /// Only the flagged fields are described. A descriptor field the schema does not declare is refused at
    /// build time (that is the point of the apply-time check), so these mirror the fixture's own types.
    /// </summary>
    private static Dictionary<string, FieldDescriptor> FieldDescriptors(string[] hiddenFields) =>
        hiddenFields.ToDictionary(
            field => field,
            field => new FieldDescriptor { Type = DescriptorTypeOf(field), Hidden = BoolOrCel.FromBoolean(true) },
            StringComparer.Ordinal);

    /// <summary>
    /// The descriptor's own field-type enum for a fixture field, resolved by <em>name</em> from the schema's.
    /// The two enums are deliberately separate types with the same member names; parsing rather than
    /// hand-mapping means this fixture cannot quietly describe a field as a type the schema does not give it.
    /// </summary>
    private static DescriptorFieldType DescriptorTypeOf(string field) => Enum.Parse<DescriptorFieldType>(
        AlvoDataFixtures.Vehicle.Fields.Single(candidate => candidate.Name == field).Type.ToString());
}
