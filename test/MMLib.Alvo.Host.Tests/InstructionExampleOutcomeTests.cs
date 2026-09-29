using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Every worked example in the assistant's instructions gets, from the real tool over the real validator, the outcome
/// it claims (D11) — and every refusal the instructions quote is one the validator really gives.
/// </summary>
/// <remarks>
/// <para>
/// The examples are the section models copy most, so an example that the framework would refuse — or accept, when it
/// claims a refusal — teaches the wrong thing with the authority of the instructions. The patch half of the claim is
/// <c>AssistantInstructionsTests</c>'; this is the validity half, which needs the core.
/// </para>
/// <para>
/// <b>The example is sent through the registered tool, not the pipeline behind it</b>, so the claimed outcome is
/// compared with what the model would really read: every member the example claims must be there, equal — except a
/// violation's <c>message</c> and <c>fix</c>, which are fragments the real ones contain (the validator wraps a compiler
/// refusal in the field it concerns), and <c>revision</c>, which is illustrative (D12).
/// </para>
/// </remarks>
public sealed partial class InstructionExampleOutcomeTests
{
    private const string Project = "bike-workshop";
    private const string Customers = "/entities/customers/fields/";
    private const string OrderLines = "/entities/order_lines/fields/";

    /// <summary>
    /// One refused computed field per refusal the instructions quote — each quote must be a fragment of what one of
    /// these draws from the validator.
    /// </summary>
    private static readonly (string Path, string Field)[] _refusalProbes =
    [
        (Customers + "probe_null", """{"type": "string", "computed": "first_name + ' ' + street"}"""),
        (Customers + "probe_mixed", """{"type": "string", "computed": "first_name + bikes_count"}"""),
        (Customers + "probe_both_guard", """{"type": "string", "computed": "has(street) && has(city) ? street + ' ' + city : ''"}"""),
        (Customers + "probe_unbounded", """{"type": "string", "maxLength": 200, "computed": "first_name + ' ' + (has(notes) ? notes : '')"}"""),
        (Customers + "probe_too_long", """{"type": "string", "maxLength": 100, "computed": "first_name + ' ' + last_name"}"""),
        (Customers + "probe_boolean", """{"type": "boolean", "computed": "has(street)"}"""),
        (Customers + "probe_compared", """{"type": "string", "computed": "first_name == 'Jana' ? first_name : last_name"}"""),
        (Customers + "probe_integer", """{"type": "integer", "computed": "first_name + ' ' + last_name"}"""),
        (Customers + "probe_constant", """{"type": "string", "computed": "'always the same'"}"""),
        (OrderLines + "probe_rate", """{"type": "decimal", "precision": 12, "scale": 2, "computed": "unit_price * 1.2"}"""),
        (OrderLines + "probe_one_plus", """{"type": "decimal", "precision": 12, "scale": 2, "computed": "unit_price * (1 + quantity)"}"""),
        (OrderLines + "probe_computed", """{"type": "decimal", "precision": 12, "scale": 2, "computed": "line_total + unit_price"}"""),
    ];

    /// <summary>
    /// The guarded joins the computed section teaches as accepted — the nested form verbatim, the middle-name forms
    /// over <c>customers</c>' own optional <c>street</c>.
    /// </summary>
    private static readonly string[] _acceptedJoins =
    [
        "has(street) ? (has(city) ? street + ' ' + city : street) : (has(city) ? city : '')",
        "has(street) ? first_name + ' ' + street : first_name",
        "!has(street) ? first_name : first_name + ' ' + street",
        "first_name + (has(street) ? ' ' + street : '') + ' ' + last_name",
    ];

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

        var outcome = await InvokeAsync(management, example);

        AssertClaims(example, outcome);
    }

    /// <summary>
    /// Holds <paramref name="outcome"/> to what <paramref name="example"/> claims: every claimed member equal, except
    /// <c>revision</c>, and every claimed violation among the real ones.
    /// </summary>
    internal static void AssertClaims(InstructionExample example, JsonElement outcome)
    {
        foreach (var claimed in example.Outcome.EnumerateObject().Where(member => member.Name is not ("violations" or "revision")))
        {
            outcome.TryGetProperty(claimed.Name, out var actual).ShouldBeTrue($"the outcome has no '{claimed.Name}': {outcome}");
            JsonElement.DeepEquals(actual, claimed.Value).ShouldBeTrue($"'{claimed.Name}' is {actual}, not {claimed.Value}");
        }

        foreach (var claimed in example.ClaimedViolations)
        {
            Violations(outcome).ShouldContain(actual => Matches(actual, claimed), $"{claimed} is not among {outcome}");
        }
    }

    [Fact]
    public async Task Every_refusal_the_computed_section_quotes_is_one_the_validator_gives()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = Administrator();
        var said = await RefusalsDrawnByProbesAsync(management);

        var quotes = Quote().Matches(ComputedSection()).Select(match => Collapsed(match.Groups["quote"].Value)).ToList();

        quotes.ShouldNotBeEmpty();
        quotes.ShouldAllBe(quote => said.Any(text => text.Contains(quote, StringComparison.Ordinal)), string.Join(" || ", said));
    }

    [Fact]
    public async Task Every_guarded_join_the_computed_section_teaches_is_accepted()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = Administrator();

        foreach (var join in _acceptedJoins)
        {
            var attempt = await AttemptAsync(management, Customers + "probe_join", $$"""{"type": "string", "computed": "{{join}}"}""");
            attempt.Valid.ShouldBeTrue($"{join}: {string.Join(" | ", attempt.Refusals)}");
        }
    }

    internal static async Task<DraftAttempt> AttemptAsync(IAlvoManagement management, string path, string field)
    {
        var current = await management.GetDescriptorAsync(Project, Ct);
        var operations = JsonSerializer.SerializeToElement(new[] { new { op = "add", path, value = JsonNode.Parse(field) } });
        return await DescriptorDraft.BuildAsync(management, Project, current.Revision, operations, Ct);
    }

    private static async Task<List<string>> RefusalsDrawnByProbesAsync(IAlvoManagement management)
    {
        var said = new List<string>();
        foreach (var (path, field) in _refusalProbes)
        {
            var attempt = await AttemptAsync(management, path, field);

            attempt.Valid.ShouldBeFalse($"the probe at {path} was accepted, so it no longer proves a refusal");
            said.AddRange(attempt.Violations.SelectMany(violation => new[] { violation.Message, violation.Fix ?? string.Empty }).Select(Collapsed));
        }

        return said;
    }

    internal static async Task<JsonElement> InvokeAsync(IAlvoManagement management, InstructionExample example)
    {
        var current = await management.GetDescriptorAsync(Project, Ct);
        var tool = ManagementTools.For(management, Project).Functions.Single(function => function.Name == example.Tool);
        var arguments = new AIFunctionArguments(example.Arguments.ToDictionary(
            argument => argument.Key,
            argument => (object?)(argument.Key == "baseRevision" ? JsonSerializer.SerializeToElement(current.Revision) : argument.Value)));

        var result = await tool.InvokeAsync(arguments, Ct);

        return JsonDocument.Parse(result is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString()! : result!.ToString()!).RootElement.Clone();
    }

    private static JsonElement[] Violations(JsonElement outcome) =>
        outcome.TryGetProperty("violations", out var violations) ? [.. violations.EnumerateArray()] : [];

    private static bool Matches(JsonElement actual, JsonElement claimed) =>
        claimed.EnumerateObject().All(member => actual.TryGetProperty(member.Name, out var value) && member.Name switch
        {
            "message" or "fix" => value.GetString()!.Contains(member.Value.GetString()!, StringComparison.Ordinal),
            _ => JsonElement.DeepEquals(value, member.Value),
        });

    private static string ComputedSection()
    {
        var text = AssistantInstructions.Text;
        var start = text.IndexOf("## 4. What Computed allows", StringComparison.Ordinal);
        var end = text.IndexOf("## 5.", start, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        return text[start..end];
    }

    private static string Collapsed(string text) => Whitespace().Replace(text, " ").TrimEnd('…');

    internal static string BikeWorkshop { get; } =
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json");

    internal static AlvoPrincipal Administrator() => new()
    {
        Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
        Scopes = new HashSet<ApiKeyScope>(),
        KeyId = "instruction-examples",
    };

    [GeneratedRegex("""\*"(?<quote>.+?)"\*""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Quote();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
