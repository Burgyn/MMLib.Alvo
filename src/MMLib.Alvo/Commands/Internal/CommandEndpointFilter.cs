using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The one filter on every command endpoint — thin HTTP glue around <see cref="CommandAccessJudge"/>, <see cref="CommandKeys"/>
/// and <see cref="CommandRunner"/> — in this order: the caller (401; 403 when the key's scopes exclude reading and writing the
/// bound entity, or, unbound, a wildcard write — Ruling 39); the key (422 when malformed; a replay, an in-flight or a spent
/// key answered at once — Ruling 7, scoped per credential — Ruling 34); the bound row read as the caller (404); the access
/// rule, its required context first (403 — Ruling 33); the chain depth (508); then the handler, outside any transaction (R-M7).
/// </summary>
/// <remarks>
/// A refusal before the handler gives the claimed key back. The data port's refusals a handler's own calls raise are answered
/// as the Data API answers them; a run whose claim was taken over after its lease is a 409; anything else propagates to the
/// host's exception handling (Ruling 27).
/// </remarks>
internal sealed class CommandEndpointFilter(
    CallerGate gate,
    IAlvoContextAccessor accessor,
    CommandAccessJudge judge,
    IAlvoSessions sessions,
    AlvoChainDepthLimit depth,
    CommandRunner runner,
    IServiceProviderIsService services,
    IOptions<JsonOptions> json)
{
    /// <summary>The filter delegate for one command's endpoint.</summary>
    internal EndpointFilterDelegate For(CommandRegistration command, EndpointFilterFactoryContext factory, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var inputs = CommandInputs.Of(factory.MethodInfo, services);

        return invocation => InvokeAsync(command, inputs, invocation, next);
    }

    private async ValueTask<object?> InvokeAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var admission = await gate.AdmitAsync(
            invocation.HttpContext, command.Entity ?? CallerGate.AnyEntity, DataOperation.Update, alsoRead: command.Entity is not null).ConfigureAwait(false);
        if (admission.Refusal is { } refused)
        {
            return refused;
        }

        return await CallerResolution.PublishingAsync(
            accessor, admission.Principal, invocation, _ => GuardedAsync(command, inputs, invocation, next, admission)).ConfigureAwait(false);
    }

    private async ValueTask<object?> GuardedAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next, CallerAdmission admission)
    {
        try
        {
            return await KeyedAsync(command, inputs, invocation, next, admission).ConfigureAwait(false);
        }
        catch (CommandClaimLostException)
        {
            return ProblemResultFactory.IdempotencyClaimLost();
        }
        catch (Exception exception) when (ProblemResultFactory.PortRefusalFor(exception) is { } refusal)
        {
            return refusal;
        }
    }

    private async Task<object?> KeyedAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next, CallerAdmission admission)
    {
        var http = invocation.HttpContext;
        var malformed = CommandKeys.TryRead(
            http.Request, command, admission.Context, admission.Principal?.KeyId,
            () => inputs.Fingerprint(command, http, invocation.Arguments, json.Value.SerializerOptions), out var key);
        if (malformed is not null)
        {
            return malformed;
        }

        var claimed = await runner.ClaimAsync(new CommandInvocation(command, admission.Context, null, key)).ConfigureAwait(false);
        return claimed.Early ?? await AdmittedAsync(claimed.Invocation, invocation, next).ConfigureAwait(false);
    }

    private async Task<object?> AdmittedAsync(CommandInvocation requested, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        CommandInvocation admitted;
        try
        {
            admitted = requested with { Record = await RowAsync(requested.Command, invocation.HttpContext, requested.Caller).ConfigureAwait(false) };
            judge.EnsureAdmitted(admitted.Command.Id, admitted.Record, admitted.Caller);
            depth.EnsureWithin(admitted.Caller);
        }
        catch
        {
            await runner.ReleaseAsync(requested.Key).ConfigureAwait(false);
            throw;
        }

        return await RunAsync(admitted, invocation, next).ConfigureAwait(false);
    }

    private async Task<object?> RunAsync(CommandInvocation admitted, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var run = runner.Start(admitted);
        AlvoCommandContext.Pending(invocation.HttpContext).Start(run);
        AlvoCaller.For(invocation.HttpContext).Resolve(admitted.Caller);

        return await runner.FinishAsync(run, async () => await next(invocation).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<AlvoRecord?> RowAsync(CommandRegistration command, HttpContext http, AlvoContext caller)
    {
        if (command.Entity is not { } entity)
        {
            return null;
        }

        return Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id)
            ? await sessions.For(caller).GetAsync(entity, id, http.RequestAborted).ConfigureAwait(false) ?? throw new AlvoRecordNotFoundException()
            : throw new AlvoRecordNotFoundException();
    }
}
