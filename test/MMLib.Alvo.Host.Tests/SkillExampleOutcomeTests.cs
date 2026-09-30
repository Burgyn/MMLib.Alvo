using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>A skill's worked example gets the outcome it claims from the real validator on <c>bike-workshop</c>.</summary>
/// <remarks>
/// The patch half of the claim is <c>SkillConformanceTests</c>' in <c>MMLib.Alvo.Ai.Tests</c>; this is the validity
/// half, which needs the core, and it runs through <see cref="InstructionExampleOutcomeTests"/>' own machinery so a
/// skill example is judged exactly as a base-prompt example is.
/// </remarks>
public sealed class SkillExampleOutcomeTests
{
    public static TheoryData<string, string> SkillExamples() => SkillCatalogue.Examples();

    [Theory]
    [MemberData(nameof(SkillExamples))]
    public async Task A_skill_example_gets_the_outcome_it_claims(string skill, string name)
    {
        var example = SkillCatalogue.Example(skill, name);
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        var outcome = await InstructionExampleOutcomeTests.InvokeAsync(management, example);

        InstructionExampleOutcomeTests.AssertClaims(example, outcome);
    }
}
