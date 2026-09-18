using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// Builds a <see cref="PolicyCatalog"/> from an <c>access</c> block and nothing else — one entity, three
/// declared roles — so an access fact is about the access block and not about a descriptor fixture.
/// </summary>
/// <remarks>
/// The schema comes from <see cref="DescriptorToSchemaMapper"/>'s own output rather than being built
/// beside the descriptor by hand, for the reason <see cref="PolicyCatalog"/>'s remarks give: the builder
/// cannot detect a descriptor and a schema that disagree, so the probe keeps them consistent by
/// construction.
/// </remarks>
internal static class PolicyCatalogBuilderProbe
{
    /// <summary>Builds the catalogue, failing the calling fact with every error when it does not build.</summary>
    /// <param name="access">The <c>access</c> block to compile, or <see langword="null"/> for none.</param>
    /// <param name="listRule">The entity's <c>rules.list</c>, or <see langword="null"/> for none.</param>
    internal static PolicyCatalog Build(Access? access, string? listRule = null)
    {
        TryBuild(access, listRule, out var catalog, out var errors).ShouldBeTrue(
            string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));
        return catalog!;
    }

    /// <summary>Asserts the catalogue does not build, and hands back every error it reported.</summary>
    /// <param name="access">The <c>access</c> block to compile, or <see langword="null"/> for none.</param>
    /// <param name="listRule">The entity's <c>rules.list</c>, or <see langword="null"/> for none.</param>
    internal static IReadOnlyList<DescriptorValidationError> Refuse(Access? access, string? listRule = null)
    {
        TryBuild(access, listRule, out _, out var errors).ShouldBeFalse();
        return errors;
    }

    private static bool TryBuild(
        Access? access,
        string? listRule,
        out PolicyCatalog? catalog,
        out IReadOnlyList<DescriptorValidationError> errors)
    {
        var descriptor = Descriptor(access, listRule);
        var schema = DescriptorToSchemaMapper.Map(descriptor);

        return PolicyCatalog.TryBuild(descriptor, schema, CelFixtures.Compiler, out catalog, out errors);
    }

    private static AlvoDescriptor Descriptor(Access? access, string? listRule) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "access-probe",
        Auth = new MMLib.Alvo.Descriptor.Auth { Roles = ["manager", "editor", "sales"] },
        Access = access,
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["deals"] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["owner_id"] = new() { Type = FieldType.Uuid },
                    ["stage"] = new() { Type = FieldType.String },
                },
                Rules = listRule is null ? null : new AccessRules { List = listRule },
            },
        },
    };
}
