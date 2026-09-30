using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>Which descriptor skill a refused pointer belongs to (D50) — the routing the eval's skill grade shares.</summary>
public sealed class SkillAreasTests
{
    public static TheoryData<string, string?> Pointers() => new()
    {
        { "/entities/x/rules/update", "rules-and-cel" },
        { "/entities/x/hooks/beforeUpdate/0/condition", "hooks" },
        { "/entities/x/indexes/0", "indexes" },
        { "/entities/x/audit", "traits-and-tenancy" },
        { "/entities/x/fields/created_at", "traits-and-tenancy" },
        { "/entities/x/fields/total/computed", "computed-and-rollups" },
        { "/entities/x/fields/notes", "entities-and-fields" },
        { "/access", "project-access" },
        { "#/entities/x", null },
        { "", null },
    };

    public static TheoryData<string> ManagedColumns() =>
        [.. AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Pointers))]
    public void A_violations_pointer_names_its_skills_area(string path, string? area) =>
        SkillAreas.ForViolation(path).ShouldBe(area);

    /// <summary>A declared managed column is a traits question, whichever trait adds it — never an ordinary field.</summary>
    [Theory]
    [MemberData(nameof(ManagedColumns))]
    public void A_managed_column_is_a_traits_and_tenancy_violation(string column) =>
        SkillAreas.ForViolation($"/entities/x/fields/{column}").ShouldBe("traits-and-tenancy");
}
