using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The "one answer per field type" invariant between the filter path and the CEL path. A filter is not CEL,
/// so there is no compiled expression to read a resolved type off — the mapping is duplicated here, and this
/// suite is what stops the copy drifting: for every field type, the type this map reports must be the type
/// the real <see cref="ICelCompiler"/> resolves for a reference to a field of that type.
/// </summary>
/// <remarks>
/// It matters because the type picks the dialect's value repair. A filter that called a decimal column
/// <c>Int</c> would compare it lexicographically on SQLite — the same fail-open a rule gating on an amount
/// had — while the identical rule, going through the compiler, answered correctly.
/// </remarks>
public class FieldCelTypeTests
{
    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Enum)]
    [InlineData(FieldType.Integer)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Boolean)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.DateTime)]
    [InlineData(FieldType.Uuid)]
    [InlineData(FieldType.Ref)]
    [InlineData(FieldType.Json)]
    public void The_shared_mapping_is_the_one_the_cel_compiler_resolves(FieldType type)
        => CelFieldType.Of(Field(type)).ShouldBe(CompilerResolvedType(type));

    [Fact]
    public void A_field_is_required()
        => Should.Throw<ArgumentNullException>(() => CelFieldType.Of((FieldSchema)null!));

    /// <summary>
    /// <c>has(probe)</c> resolves the field's own type for every field type, where a comparison would first
    /// have to be legal for it — which is the point: this asks the compiler what the column's type is, not
    /// what a comparison over it would promote to.
    /// </summary>
    private static CelValueType CompilerResolvedType(FieldType type)
    {
        var entity = new EntitySchema
        {
            Name = "probes",
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }, Field(type)],
        };

        var compiled = CelFixtures.Compiler.Compile("has(probe)", CelProfile.Rule, entity);
        compiled.IsSuccess.ShouldBeTrue(
            $"'has(probe)' over a {type} field did not compile: "
            + string.Join("; ", compiled.Errors.Select(error => error.Message)));

        return compiled.Expression!.Root.ShouldBeOfType<CelHas>().Field.Type;
    }

    private static FieldSchema Field(FieldType type) => new()
    {
        Name = "probe",
        Type = type,
        Nullable = true,
        Precision = type == FieldType.Decimal ? 18 : null,
        Scale = type == FieldType.Decimal ? 2 : null,
        EnumValues = type == FieldType.Enum ? ["one", "two"] : null,
        Reference = type == FieldType.Ref ? new RefSchema("probes", OnDelete.Restrict) : null,
    };
}
