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

    [Fact]
    public async Task The_function_cases_wrong_answer_is_refused_as_unhonoured()
    {
        var attempt = await AttemptAsync(SkillCaseAnswers.FunctionHook);

        attempt.Valid.ShouldBeFalse();
        attempt.Refusals.ShouldContain(
            refusal => refusal.Contains(UnhonouredFeatures.UnhonouredAction("function").Consequence, StringComparison.Ordinal),
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
}
