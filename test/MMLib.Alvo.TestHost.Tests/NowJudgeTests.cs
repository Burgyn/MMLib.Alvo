using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The judges that read <c>now()</c> outside the data port's own entries (HX-11 Task 2 review C1, I1): an in-process command's
/// access rule, and the embedded policy simulator's command verdicts and field masks. Each binds the flow's instant, so a
/// <c>now()</c> rule answers its verdict instead of failing as a defect, and the simulator answers what production does.
/// </summary>
public sealed class NowJudgeTests
{
    private const string Project = "now-judges";
    private const string Holds = "now() == now()";
    private const string NeverHolds = "now() != now()";

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "descriptors", "now-judges.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_in_process_command_s_now_access_rule_answers_its_verdict()
    {
        var ran = new Ran();
        await using var alvo = await StartAsync(ran);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await commands.InvokeAsync("timed_note", "x", AlvoTestHost.Caller("clerk"), Ct);
        await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("never_note", "y", AlvoTestHost.Caller("clerk"), Ct));

        ran.Inputs.ShouldBe(["x"]);
    }

    [Fact]
    public async Task The_simulator_previews_a_now_command_rule_as_the_command_judges_it()
    {
        await using var alvo = await StartAsync(new Ran());

        var verdict = await SimulateAsync(alvo);

        verdict.Commands.ToDictionary(command => command.Name, command => command.Allowed)
            .ShouldBe(new Dictionary<string, bool?> { ["timed_note"] = true, ["never_note"] = false }, ignoreOrder: true);
    }

    [Fact]
    public async Task The_simulator_masks_a_now_field_exactly_as_a_read_does()
    {
        await using var alvo = await StartAsync(new Ran(), withCommands: false);
        var clerk = AlvoTestHost.Caller("clerk");
        var row = await alvo.Data.CreateAsync("notes", new Dictionary<string, object?> { ["body"] = "b", ["secret"] = "s" }, clerk, cancellationToken: Ct);

        var verdict = await SimulateAsync(alvo, clerk);

        row.Values.ContainsKey("secret").ShouldBeTrue("production: the mask now() != now() is false, so the field is shown");
        verdict.HiddenFields.ShouldNotContain("secret", "the simulator must answer what the read did");
    }

    private static async Task<ManagementPolicyVerdict> SimulateAsync(AlvoTestHost alvo, AlvoContext? caller = null)
    {
        using var scope = alvo.Services.CreateScope();
        var ambient = scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>();
        var operatorContext = AlvoTestHost.Caller("clerk");
        ambient.Principal = new AlvoPrincipal { Context = operatorContext, Scopes = new HashSet<ApiKeyScope>(), KeyId = "now-judges" };
        var simulated = caller ?? operatorContext;

        return await scope.ServiceProvider.GetRequiredService<IAlvoManagement>().SimulatePolicyAsync(
            Project,
            new ManagementPolicySimulation("notes", "get", new ManagementSimulatedCaller(simulated.User.Value, ["clerk"])),
            Ct);
    }

    private static Task<AlvoTestHost> StartAsync(Ran ran, bool withCommands = true) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton(ran);
            builder.FromDescriptor(Descriptor);
            if (withCommands)
            {
                builder.AddCommand<RecordNote>("timed_note", Holds).AddCommand<RecordNote>("never_note", NeverHolds);
            }
        }, cancellationToken: Ct);

    public sealed class Ran
    {
        public ConcurrentQueue<string> Inputs { get; } = new();
    }

    public sealed class RecordNote(Ran ran) : IAlvoCommand<string>
    {
        public Task HandleAsync(string input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            ran.Inputs.Enqueue(input);
            return Task.CompletedTask;
        }
    }
}
