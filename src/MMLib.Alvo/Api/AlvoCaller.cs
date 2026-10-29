using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace MMLib.Alvo.Api;

/// <summary>
/// The caller Alvo resolved for this request, as a minimal-API handler parameter.
/// </summary>
/// <remarks>
/// <para>
/// Take it as a parameter on an endpoint mapped with <c>.WithAlvoCaller()</c>, on a command or on a webhook receiver; pass
/// <see cref="Context"/> to <c>IAlvoData</c>, <c>IAlvoSessions.For</c> or <c>IAlvoTransactions</c>. A request with no
/// credential binds <see cref="AlvoContext.Anonymous"/>; a credential that cannot be used is answered 401 before the
/// handler runs — the rules the generated Data API applies.
/// </para>
/// <para>
/// Minimal APIs bind parameters before endpoint filters run, so this object is bound first and filled by Alvo's filter
/// afterwards. On an endpoint no Alvo filter resolves, reading <see cref="Context"/> throws rather than answering as
/// anonymous, so a forgotten <c>.WithAlvoCaller()</c> is a loud error, never a silently unauthenticated endpoint.
/// </para>
/// </remarks>
public sealed class AlvoCaller : IBindableFromHttpContext<AlvoCaller>
{
    private static readonly object _slot = new();
    private AlvoContext? _context;

    private AlvoCaller()
    {
    }

    /// <summary>Gets the resolved caller.</summary>
    /// <exception cref="InvalidOperationException">No Alvo filter resolved a caller for this endpoint.</exception>
    public AlvoContext Context => _context ?? throw new InvalidOperationException(
        "No caller was resolved for this request: the endpoint takes an AlvoCaller but was not mapped with .WithAlvoCaller(), "
        + "MapAlvoCommand or MapAlvoWebhookReceiver. Add .WithAlvoCaller() to the endpoint.");

    /// <summary>Binds the request's caller slot; Alvo's filter fills it before the handler runs.</summary>
    /// <param name="context">The request.</param>
    /// <param name="parameter">The handler parameter being bound.</param>
    /// <returns>The request's caller.</returns>
    public static ValueTask<AlvoCaller?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult<AlvoCaller?>(For(context));
    }

    /// <summary>The request's one caller slot, created on first use.</summary>
    /// <param name="http">The request.</param>
    internal static AlvoCaller For(HttpContext http)
    {
        if (http.Items.TryGetValue(_slot, out var existing) && existing is AlvoCaller caller)
        {
            return caller;
        }

        var created = new AlvoCaller();
        http.Items[_slot] = created;
        return created;
    }

    /// <summary>Fills the slot with the resolved caller.</summary>
    /// <param name="context">The caller.</param>
    internal void Resolve(AlvoContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));
}
