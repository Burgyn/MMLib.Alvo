using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// One generated route's proof that Alvo's authorization filter was composed into its request delegate.
/// </summary>
/// <remarks>
/// <para>
/// The gate is added through a factory this type owns, and the factory counts each time ASP.NET Core composes it into the
/// filter pipeline — which it does while it builds the endpoint, after every route and group convention has run and
/// before any finally convention does (measured on .NET 10: a finally convention that clears <c>FilterFactories</c>
/// leaves the composed gate in force, <c>GateWitnessTests</c>). So the count answers "did the factory compose Alvo's gate", not "is something in <c>FilterFactories</c> that looks like
/// it": a convention that clears the list, or rebuilds it with look-alike factories, leaves the count where it was. A
/// convention that assigns <c>EndpointBuilder.RequestDelegate</c> outright replaces the handler as well as the gate; that
/// endpoint no longer reaches Alvo's code at all, and the witness does not claim to see it (Ruling 49).
/// </para>
/// <para>
/// It rides on the endpoint as metadata, so the data source can read it back from each built endpoint. A route-level
/// <c>Finally</c> check was the issue's proposal; it is not used because whether a group's finally conventions run before
/// or after a route's is a framework detail, not a contract, so a <c>MapGroup(…).Finally(…)</c> could undo the check. The
/// witness does not depend on any order.
/// </para>
/// </remarks>
/// <param name="route">The route it belongs to, as a refusal names it: <c>create on 'owners'</c>, <c>command 'confirm_stay'</c>.</param>
internal sealed class AlvoGateWitness(string route)
{
    private int _compositions;

    /// <summary>The witness of one generated Data API route.</summary>
    internal AlvoGateWitness(string entity, DataApiEndpointKind kind)
        : this($"{kind.ToWireName()} on '{entity}'")
    {
    }

    /// <summary>Gets how many times the gate has been composed into a request delegate.</summary>
    internal int Compositions => Volatile.Read(ref _compositions);

    /// <summary>Gets the route this witness belongs to, for a refusal message.</summary>
    internal string Route => route;

    /// <summary>The filter factory that composes <paramref name="gate"/> and counts doing so.</summary>
    /// <param name="gate">Alvo's authorization filter for this route.</param>
    internal Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> Wrap(IEndpointFilter gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return Wrap((_, next) => invocation => gate.InvokeAsync(invocation, next));
    }

    /// <summary>The filter factory that composes <paramref name="factory"/> — a command's gate — and counts doing so.</summary>
    /// <param name="factory">Alvo's filter factory for this route.</param>
    internal Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> Wrap(
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return (context, next) =>
        {
            Interlocked.Increment(ref _compositions);
            return factory(context, next);
        };
    }
}
