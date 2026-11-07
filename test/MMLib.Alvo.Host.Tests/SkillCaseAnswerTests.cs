using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Each proposing skill case of the eval (spec §7.5) has a right answer the real validator accepts on
/// <c>bike-workshop</c>, and the <c>function</c> case's wrong answer is refused as unhonoured.
/// </summary>
/// <remarks>
/// A case whose right answer the validator refused would grade every model a failure. The patches are
/// <see cref="SkillCaseAnswers"/>, the same ones <c>SkillCasesTests</c> (in <c>MMLib.Alvo.Ai.Eval.Tests</c>) builds its
/// right turns from.
/// </remarks>
public sealed class SkillCaseAnswerTests
{
    public static TheoryData<string> Cases() => [.. SkillCaseAnswers.RightAnswers.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_skill_cases_right_answer_passes_the_real_dry_run(string name)
    {
        var attempt = await AttemptAsync(SkillCaseAnswers.RightAnswers[name]);

        attempt.Valid.ShouldBeTrue($"{name}: {string.Join(" | ", attempt.Refusals)}");
    }

    /// <summary>
    /// <c>task_management_workers</c>' right answer is valid on the real dry run and draws no warning (D52 puts any on a
    /// valid attempt's violations): no managed column, and every owner clause on a ref to users.
    /// </summary>
    [Fact]
    public async Task The_task_management_right_answer_passes_the_real_dry_run_with_no_warning()
    {
        var attempt = await AttemptAsync(SkillCaseAnswers.TaskManagement);

        attempt.Valid.ShouldBeTrue(string.Join(" | ", attempt.Refusals));
        attempt.Violations.Where(violation => violation.Severity == "warning" && !IsUnstoredSecret(violation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_function_cases_wrong_answer_is_refused_as_an_unregistered_function()
    {
        var attempt = await AttemptAsync(SkillCaseAnswers.FunctionHook);

        attempt.Valid.ShouldBeFalse();
        attempt.Refusals.ShouldContain(
            refusal => refusal.Contains("'invoice' is not a function this host registers.", StringComparison.Ordinal),
            string.Join(" | ", attempt.Refusals));
    }

    private static async Task<Ai.Internal.DraftAttempt> AttemptAsync(string operations)
    {
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        using var document = JsonDocument.Parse(operations);
        return await InstructionExampleOutcomeTests.AttemptAsync(management, document.RootElement.Clone());
    }

    /// <summary>The bike-workshop endpoint names a secret no test host stores; that warning is the secret check's, not the case's.</summary>
    private static bool IsUnstoredSecret(MMLib.Alvo.Ai.Internal.ToolViolation violation) => violation.Pointer.EndsWith("/secretRef", StringComparison.Ordinal);
}
