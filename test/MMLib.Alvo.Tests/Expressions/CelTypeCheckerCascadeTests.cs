using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// One error per independent problem — the checker's own contract, and the half of it nothing else
/// pins. A node that already failed tells its parent so, and the parent then refuses to derive a
/// second error from the first one's wreckage; a node that passed says so just as precisely, so the
/// parent's own rule still fires. Both directions are asserted here, because a flag that is stuck
/// on hides real refusals (an ill-typed expression compiles) and a flag stuck off buries the one
/// error an agent has to fix under derived noise.
/// </summary>
public class CelTypeCheckerCascadeTests
{
    /// <summary>
    /// Each row carries exactly one authored mistake inside a parent that would have its own
    /// opinion about the operand's type. The parent must stay quiet: one mistake, one error.
    /// </summary>
    [Theory]
    [InlineData("-(!status)", CelProfile.Computed)]
    [InlineData("-nope", CelProfile.Computed)]
    [InlineData("-status + 1", CelProfile.Computed)]
    [InlineData("(status && true) + 1", CelProfile.Computed)]
    [InlineData("'x' in nope", CelProfile.Rule)]
    [InlineData("(nope == 1) + 1", CelProfile.Computed)]
    [InlineData("(status == 1) + 1", CelProfile.Computed)]
    [InlineData("has(nope) + 1", CelProfile.Computed)]
    [InlineData("true ? 1 : nope", CelProfile.Computed)]
    [InlineData("(true ? 'a' : nope) + 1", CelProfile.Computed)]
    [InlineData("(true ? 'a' : 1) + 1", CelProfile.Computed)]
    [InlineData("(true ? 1 : 2) == 'x'", CelProfile.Rule)]
    [InlineData("changed(nope) == 'x'", CelProfile.Condition)]
    [InlineData("((status + 1) && true) ? 1 : 2", CelProfile.Computed)]
    [InlineData("((nope + 1) && true) ? 1 : 2", CelProfile.Computed)]
    [InlineData("(total + total) == 'x'", CelProfile.Rule)]
    public void A_failed_operand_does_not_also_report_its_parents_derived_error(string source, CelProfile profile)
    {
        Compile(source, profile).Errors.ShouldHaveSingleItem();
    }

    /// <summary>
    /// The other side of the same rule: two independent mistakes are two errors — never one folded
    /// into the other, and never a third derived from either.
    /// </summary>
    [Theory]
    [InlineData("('x' in nope) + 1", CelProfile.Computed)]
    [InlineData("('x' in status) + 1", CelProfile.Computed)]
    [InlineData("(total ? 1 : 2) && true", CelProfile.Computed)]
    [InlineData("(status && total) == 1", CelProfile.Rule)]
    [InlineData("lowerAscii(total) == 1", CelProfile.Mutate)]
    [InlineData("lowerAscii(title) == 1", CelProfile.Mutate)]
    public void Two_independent_problems_report_exactly_two_errors(string source, CelProfile profile)
    {
        Compile(source, profile).Errors.Count.ShouldBe(2);
    }

    /// <summary>
    /// A well-typed operand is never marked "already in error", so the parent's own type rule still
    /// runs over it. Each row is an expression whose only refusal comes from a parent looking at a
    /// perfectly good child — the case a too-eager error flag would silently let compile.
    /// </summary>
    [Theory]
    [InlineData("(is_public && true) == 1", CelProfile.Rule)]
    [InlineData("(true ? 1 : 2) && true ? 1 : 2", CelProfile.Computed)]
    [InlineData("(total + 1) == 'x' ? 1 : 2", CelProfile.Computed)]
    public void A_healthy_operand_does_not_suppress_its_parents_own_refusal(string source, CelProfile profile)
    {
        Compile(source, profile).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// A field the schema types with something this compiler does not know is refused per
    /// reference, and the comparison over the two unresolved sides adds nothing of its own — in
    /// particular not the "compared against a null literal" refusal its placeholder type would
    /// otherwise trip.
    /// </summary>
    [Fact]
    public void An_unrecognized_field_type_reports_one_error_per_reference_and_nothing_derived()
    {
        var entity = new EntitySchema
        {
            Name = "weird",
            Fields = [new FieldSchema { Name = "mystery", Type = (FieldType)999 }],
        };

        var result = CelFixtures.Compiler.Compile("mystery == mystery", CelProfile.Rule, entity);

        result.Errors.Count.ShouldBe(2);
    }

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, CelFixtures.Orders);
}
