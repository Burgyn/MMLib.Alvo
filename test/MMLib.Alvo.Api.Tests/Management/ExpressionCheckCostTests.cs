using MMLib.Alvo.Management;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// What one expression check costs on the <c>bike-workshop</c> descriptor. Opt-in: <c>ALVO_MEASURE=1</c>.
/// </summary>
/// <remarks>
/// A measurement that records, it does not gate on a number: the figures it prints decide the dashboard's debounce
/// (spec §5, criterion 5). The check is called in-process, as the dashboard calls it, so no HTTP is measured.
/// </remarks>
public sealed class ExpressionCheckCostTests(ExpressionCheckAgreementTests.World fixture)
    : IClassFixture<ExpressionCheckAgreementTests.World>
{
    private const string Slot = "/entities/service_orders/rules/update";
    private const string Valid = "'admin' in @user.roles || assigned_user_id == @user.id";
    private const string Invalid = "'amdin' in @user.roles || assigned_user_id == @user.id";
    private const int Warmup = 20;
    private const int Calls = 200;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Measures the descriptor as shipped.</summary>
    /// <returns>A task that completes when the figures are printed.</returns>
    [Fact]
    public Task Check_cost_on_the_bike_workshop_descriptor() => MeasureAsync("bike-workshop", 1);

    /// <summary>Measures a descriptor with its entities duplicated three times, to show how the cost scales.</summary>
    /// <returns>A task that completes when the figures are printed.</returns>
    [Fact]
    public Task Check_cost_on_a_four_times_larger_descriptor() => MeasureAsync("bike-workshop x4", 4);

    private async Task MeasureAsync(string label, int copies)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ALVO_MEASURE") == "1",
            "A measurement, not a check: set ALVO_MEASURE=1 to run it.");

        var descriptor = Descriptor(copies);
        var management = fixture.Management();
        for (var i = 0; i < Warmup; i++)
        {
            await CheckAsync(management, descriptor, i);
        }

        var millis = new double[Calls];
        for (var i = 0; i < Calls; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await CheckAsync(management, descriptor, i);
            millis[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Report(label, descriptor.Length, millis);
    }

    private static async Task CheckAsync(IAlvoManagement management, string descriptor, int i)
    {
        var source = i % 2 == 0 ? Valid : Invalid;
        var verdict = await management.CheckExpressionAsync(
            "expression-agreement", new ManagementExpressionCheck(descriptor, Slot, source), Ct);
        Assert.Equal(i % 2 == 0, verdict.IsValid);
    }

    private static string Descriptor(int copies)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var entities = root["entities"]!.AsObject();
        var names = entities.Select(e => e.Key).ToList();
        for (var copy = 2; copy <= copies; copy++)
        {
            foreach (var name in names)
            {
                var clone = entities[name]!.DeepClone();
                Rename(clone, names, copy);
                entities[$"{name}_{copy}"] = clone;
            }
        }

        return root.ToJsonString();
    }

    // A copy must be a descriptor that validates on its own terms, else the validator stops before it reaches the slot
    // and the measurement would time a refusal; so a copy's refs and rollups point at the copy's own entities.
    private static void Rename(JsonNode? node, List<string> names, int copy)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (key is "entity" or "from" && child?.GetValue<string>() is { } target && names.Contains(target))
                    {
                        obj[key] = $"{target}_{copy}";
                    }
                    else
                    {
                        Rename(child, names, copy);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Rename(child, names, copy);
                }

                break;
        }
    }

    private static void Report(string label, int chars, double[] millis)
    {
        Array.Sort(millis);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[ALVO_MEASURE] expression check, {label}: descriptor {chars / 1024.0:F1} KB, {millis.Length} calls "
            + $"p50 {Percentile(millis, 0.50):F2} ms, p95 {Percentile(millis, 0.95):F2} ms, max {millis[^1]:F2} ms");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
}
