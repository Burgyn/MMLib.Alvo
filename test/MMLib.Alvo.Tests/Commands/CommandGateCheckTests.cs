using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The start-time check that every mapped command composed its gate (Ruling 38): a refusal throws, records the reason on
/// <see cref="AlvoBootState"/> for readiness, and logs it at Critical.
/// </summary>
public sealed class CommandGateCheckTests
{
    private readonly AlvoBootState _state = new();

    [Fact]
    public void A_command_whose_gate_was_never_composed_refuses_the_start_records_it_and_logs_it()
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        var refusal = Should.Throw<AlvoStartupRefusedException>(() => Check(Endpoint(composed: false), loggers).EnsureGated());

        refusal.Message.ShouldContain("removed Alvo's filter from command 'quote_stay'");
        _state.Phase.ShouldBe(AlvoBootPhase.Failed);
        _state.Failure.ShouldBe(refusal.Message);
        capturing.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Critical);
    }

    [Fact]
    public void A_command_whose_gate_was_composed_starts()
    {
        using var loggers = LoggerFactory.Create(_ => { });

        Should.NotThrow(() => Check(Endpoint(composed: true), loggers).EnsureGated());

        _state.Failure.ShouldBeNull();
    }

    [Fact]
    public void The_startup_filter_checks_after_the_pipeline_it_wraps_is_built()
    {
        using var loggers = LoggerFactory.Create(_ => { });
        var built = false;

        var configure = Check(Endpoint(composed: false), loggers).Configure(_ => built = true);

        Should.Throw<AlvoStartupRefusedException>(() => configure(null!));
        built.ShouldBeTrue("the host's pipeline — which hands the routing composite its sources — is built first");
    }

    private CommandGateCheck Check(Endpoint endpoint, ILoggerFactory loggers)
    {
        var commands = new CommandRegistry();
        commands.Map("quote_stay", "/commands/quote", null, CommandAccess.Of("true"));
        var services = new ServiceCollection()
            .AddSingleton<EndpointDataSource>(new CompositeEndpointDataSource([new DefaultEndpointDataSource(endpoint)]))
            .BuildServiceProvider();

        return new CommandGateCheck(commands, services, _state, loggers.CreateLogger<CommandGateCheck>());
    }

    private static Endpoint Endpoint(bool composed)
    {
        var witness = new AlvoGateWitness("command 'quote_stay'");
        if (composed)
        {
            _ = witness.Wrap((_, next) => next)(null!, _ => ValueTask.FromResult<object?>(null));
        }

        return new Endpoint(
            _ => Task.CompletedTask, new EndpointMetadataCollection(new CommandEndpointMetadata("quote_stay", null), witness), "quote_stay");
    }
}
