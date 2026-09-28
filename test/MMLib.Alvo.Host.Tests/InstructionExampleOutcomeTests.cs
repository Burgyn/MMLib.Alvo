using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Every worked example in the assistant's instructions gets, from the real validator, the outcome it claims (D11).
/// </summary>
/// <remarks>
/// The examples are the section models copy most, so an example that the framework would refuse — or accept, when it
/// claims a refusal — teaches the wrong thing with the authority of the instructions. The patch half of the claim is
/// <c>AssistantInstructionsTests</c>'; this is the validity half, which needs the core. A claimed violation's message
/// is a fragment the real one contains, because the validator wraps a compiler refusal in the field it concerns.
/// </remarks>
public sealed class InstructionExampleOutcomeTests
{
    private const string Project = "bike-workshop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> ExampleNames() =>
        [.. InstructionExamples.Parse(AssistantInstructions.Text).Select(example => example.Name)];

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public async Task A_worked_example_gets_the_outcome_the_instructions_claim(string name)
    {
        var example = InstructionExamples.Parse(AssistantInstructions.Text).Single(candidate => candidate.Name == name);
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = Administrator();

        var current = await management.GetDescriptorAsync(Project, Ct);
        var attempt = await DescriptorDraft.BuildAsync(management, Project, current.Revision, example.Operations, Ct);

        attempt.Valid.ShouldBe(example.ClaimsValid, string.Join(" | ", attempt.Refusals));
        attempt.ChangedPaths.ShouldBe(example.ChangedPaths);
        foreach (var (source, fragment) in example.ClaimedViolations)
        {
            attempt.Violations.ShouldContain(
                violation => violation.Source == source && violation.Message.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private static string BikeWorkshop { get; } =
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json");

    private static AlvoPrincipal Administrator() => new()
    {
        Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
        Scopes = new HashSet<ApiKeyScope>(),
        KeyId = "instruction-examples",
    };
}
