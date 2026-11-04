using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Internal commands (spec 2026-10-14 §4.9, acceptance (8)): invoked by handlers, timers and jobs through
/// <see cref="IAlvoCommands"/>, under the same access rule, unit and completion event as a mapped command.
/// </summary>
public sealed class InternalCommandTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_internal_command_invoked_from_a_handler_runs_its_access_rule_and_emits_its_event()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.Count.ShouldBe(1, "the system the handler runs as holds admin, which the rule admits");
        seen.Completions.ShouldBe(["escalate_ticket"]);
    }

    /// <summary>Any handler failure would empty the queues; the refusal's type is pinned by the next fact.</summary>
    [Fact]
    public async Task A_handler_running_as_a_caller_the_rule_refuses_never_runs_the_command()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.EventActor);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.ShouldBeEmpty("an agent holds no admin, so the access rule refuses before the command runs");
        seen.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_the_rule_refuses_is_an_authorization_refusal()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("escalate_ticket", new Escalation(Guid.NewGuid()), AlvoTestHost.Caller("agent"), Ct));
    }

    [Fact]
    public async Task A_negated_identity_rule_refuses_the_anonymous_caller()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("unbanned_escalation", new Escalation(Guid.NewGuid()), AlvoContext.Anonymous, Ct)))
            .Message.ShouldContain("has no identity");
    }

    [Fact]
    public async Task An_unknown_command_and_a_wrong_input_are_refused_by_name()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct))).ParamName.ShouldBe("name");
        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate_ticket", "not an escalation", AlvoContext.System(null), Ct))).ParamName.ShouldBe("input");
    }

    [Fact]
    public async Task Invoking_a_command_inside_an_open_unit_is_refused_before_it_runs()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        var refused = await Should.ThrowAsync<AlvoUnitInProgressException>(() => alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            AlvoContext.System(null), (_, ct) => commands.InvokeAsync("note_escalation", new Escalation(Guid.NewGuid()), AlvoContext.System(null), ct), cancellationToken: Ct));

        refused.Message.ShouldStartWith("IAlvoCommands.InvokeAsync");
        seen.Escalated.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_past_the_chain_depth_limit_is_refused_before_the_command_runs()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();
        var deep = AlvoContext.System(null) with { Provenance = new AlvoProvenance { Depth = 9 } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => commands.InvokeAsync("note_escalation", new Escalation(Guid.NewGuid()), deep, Ct));

        seen.Escalated.ShouldBeEmpty();
    }

    /// <summary>
    /// An internal command is one hop deeper than its caller, so a command that invokes itself with its own caller meets the
    /// chain-depth limit — a refusal, never a stack overflow. The command stops itself well past the limit, so the fact
    /// fails rather than crashing the run should the limit not hold.
    /// </summary>
    [Fact]
    public async Task A_command_invoking_itself_is_stopped_by_the_chain_depth_limit()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => commands.InvokeAsync("recurse", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct));

        seen.Escalated.Count.ShouldBe(9, "depths 0 through 8 run; the ninth hop is past the default limit of 8");
    }

    [Fact]
    public async Task A_command_that_opens_no_unit_still_records_its_completion()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await commands.InvokeAsync("note_escalation", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.Count.ShouldBe(1);
        seen.Completions.ShouldBe(["note_escalation"]);
    }

    [Fact]
    public async Task A_lambda_access_rule_admits_the_role_it_names_and_refuses_the_rest()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await commands.InvokeAsync("admin_note", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct);
        await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("admin_note", new Escalation(Guid.NewGuid()), AlvoTestHost.Caller("agent"), Ct));

        seen.Escalated.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_value_input_the_command_cannot_be_cast_to_is_refused_by_name()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("count_anything", 42, AlvoContext.System(null), Ct))).ParamName.ShouldBe("input");
    }

    [Fact]
    public void A_class_that_is_no_command_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddAlvo(alvo => alvo.AddCommand<Seen>("not_a_command", "true")))
            .Message.ShouldContain("IAlvoCommand<TInput>");

    [Fact]
    public void An_abstract_command_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddAlvo(alvo => alvo.AddCommand<AbstractCommand>("abstract_command", "true")))
            .Message.ShouldContain("concrete");

    private static Task<AlvoTestHost> StartAsync(Seen seen, AlvoRunAs runAs) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton(seen);
            builder.FromDescriptor(HelpDesk)
                .AddCommand<EscalateTicket>("escalate_ticket", "'admin' in @user.roles")
                .AddCommand<EscalateTicket>("unbanned_escalation", "!(string(@user.id) == '6c1f9a3e-0000-4000-8000-000000000001')")
                .AddCommand<NoteEscalation>("note_escalation", "'admin' in @user.roles")
                .AddCommand<NoteEscalation>("admin_note", caller => caller.HasRole("admin"))
                .AddCommand<CountAnything>("count_anything", "true")
                .AddCommand<Recurse>("recurse", "true")
                .Entity("tickets", tickets => tickets.OnCreated(
                    "escalate-new",
                    (AlvoCreated<AlvoRecord> created, IAlvoCommands commands, AlvoEventContext context, CancellationToken ct) =>
                        commands.InvokeAsync("escalate_ticket", new Escalation(Guid.Parse((string)created.New["id"]!)), context.Caller, ct),
                    configure: options => options.RunAs = runAs))
                .AddEventHandler("on-escalated", "alvo.command.escalate_ticket.completed", (AlvoRecord payload) => Completed(seen, payload))
                .AddEventHandler("on-noted", "alvo.command.note_escalation.completed", (AlvoRecord payload) => Completed(seen, payload));
        }, cancellationToken: Ct);

    private static Task Completed(Seen seen, AlvoRecord payload)
    {
        seen.Completions.Enqueue((string)payload["command"]!);
        return Task.CompletedTask;
    }

    public sealed record Escalation(Guid Ticket);

    public sealed class NoteEscalation(Seen seen) : IAlvoCommand<Escalation>
    {
        public Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            seen.Escalated.Enqueue(input.Ticket);
            return Task.CompletedTask;
        }
    }

    public sealed class Recurse(Seen seen, IAlvoCommands commands) : IAlvoCommand<Escalation>
    {
        private const int Runaway = 50;

        public async Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            seen.Escalated.Enqueue(input.Ticket);
            if (seen.Escalated.Count < Runaway)
            {
                await commands.InvokeAsync("recurse", input, context.Caller, cancellationToken);
            }
        }
    }

    public sealed class CountAnything : IAlvoCommand<object>
    {
        public Task HandleAsync(object input, AlvoCommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public abstract class AbstractCommand : IAlvoCommand<Escalation>
    {
        public abstract Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken);
    }

    public sealed class EscalateTicket(Seen seen) : IAlvoCommand<Escalation>
    {
        public async Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: cancellationToken);
            seen.Escalated.Enqueue(input.Ticket);
        }
    }

    public sealed class Seen
    {
        public ConcurrentQueue<Guid> Escalated { get; } = new();

        public ConcurrentQueue<string> Completions { get; } = new();
    }
}
