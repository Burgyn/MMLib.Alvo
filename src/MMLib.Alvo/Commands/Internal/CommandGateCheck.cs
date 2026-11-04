using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Refuses to start a host in which a convention removed a command's Alvo filter: every endpoint carrying a
/// <see cref="CommandEndpointMetadata"/> must carry its <see cref="AlvoGateWitness"/>, and the witness must have been
/// composed into the request delegate. Without the filter a command's handler would run with no caller, no key and no
/// access rule — whether or not it reads <c>AlvoCommandContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A startup filter, not a boot stage.</b> The host's endpoint data sources reach the routing composite only when the
/// request pipeline is built — after the boot's <c>StartingAsync</c> has primed the descriptor, and before the server
/// listens. Read during the boot, the composite is still empty and every command would pass unchecked (measured:
/// <c>GateWitnessTests</c>), so the check runs right after the pipeline is built: a refusal fails the host's start before
/// the server binds, records the reason on <see cref="AlvoBootState"/> for readiness, and logs it.
/// </para>
/// <para>
/// It reads the host's own endpoint data sources, never Alvo's Data API source, whose routes materialise on the first
/// request; a <c>MapGroup</c> holding both the Data API and a command is enumerated whole, so its Data API routes
/// materialise at start (Ruling 38 states the cost).
/// </para>
/// </remarks>
internal sealed partial class CommandGateCheck(
    CommandRegistry commands, IServiceProvider services, AlvoBootState state, ILogger<CommandGateCheck> logger) : IStartupFilter
{
    /// <inheritdoc/>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            next(app);
            EnsureGated();
        };
    }

    /// <summary>Refuses the start when a mapped command's endpoint did not compose its gate.</summary>
    /// <exception cref="AlvoStartupRefusedException">A command endpoint's gate was removed.</exception>
    internal void EnsureGated()
    {
        if (commands.Mapped.Count == 0)
        {
            return;
        }

        if (services.GetService<EndpointDataSource>() is not CompositeEndpointDataSource composite)
        {
            NoRoutingComposite(logger, commands.Mapped.Count);
            return;
        }

        if (composite.DataSources.Where(source => source is not AlvoEndpointDataSource).SelectMany(source => source.Endpoints)
            .Select(Ungated).FirstOrDefault(command => command is not null) is { } ungated)
        {
            Refuse(ungated);
        }
    }

    private static string? Ungated(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<CommandEndpointMetadata>() is { } command
        && endpoint.Metadata.GetMetadata<AlvoGateWitness>() is not { Compositions: > 0 }
            ? command.Name
            : null;

    private void Refuse(string command)
    {
        var refusal = new AlvoStartupRefusedException(
            $"Alvo cannot start: a convention removed Alvo's filter from command '{command}', so its handler would run with no caller, no Idempotency-Key and no access rule.",
            "Remove the convention that clears or rebuilds EndpointBuilder.FilterFactories on the command (or on a MapGroup it is mapped under); a convention may add filters around Alvo's, never take them away.");
        state.Failed(refusal.Message);
        CommandUngated(logger, refusal.Message);

        throw refusal;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Refusal}")]
    private static partial void CommandUngated(ILogger logger, string refusal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Commands} mapped command(s) were not checked for Alvo's gate: the host registers no routing endpoint data source to read them from.")]
    private static partial void NoRoutingComposite(ILogger logger, int commands);
}
