using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The dashboard's records, read and written as the signed-in operator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <see cref="IAlvoData"/>, not through the Management API.</b> Design §2.4 and
/// deviation D4: the Management API has no data surface, deliberately — a read through it would be
/// a second path to rows, with a second place for the tenant predicate to be forgotten. The data
/// port is the one place authorization is compiled into the SQL <c>WHERE</c>, so a dashboard that
/// reaches rows through it inherits isolation rather than reimplementing it.
/// </para>
/// <para>
/// <b>Every call carries the operator's own <see cref="AlvoContext"/>.</b> That is what makes
/// <i>"the dashboard is not a policy bypass"</i> measurable: the same caller sees exactly the rows
/// through this screen that they see through <c>/api</c>, because it is the same predicate over
/// the same port. An operator who is not admitted at all gets
/// <see cref="AlvoContext.Anonymous"/> — which reaches nothing, by default-deny.
/// </para>
/// </remarks>
/// <param name="data">The data port.</param>
/// <param name="callers">Turns the signed-in operator into the caller Alvo authorizes.</param>
/// <param name="authentication">Who is signed in on this circuit.</param>
internal sealed class DataGateway(
    IAlvoData data,
    IAlvoAdminCallerResolver callers,
    AuthenticationStateProvider authentication)
{
    private AlvoContext? _context;

    /// <summary>The operator's own caller, as the Data API would resolve it.</summary>
    /// <remarks>
    /// Exposed because two screens need to <em>say</em> what it is — the Data screen explains an
    /// empty page by naming the tenant the caller holds, and the Rules screen offers it as one of
    /// the callers to simulate. Neither of them decides anything with it.
    /// </remarks>
    public async ValueTask<AlvoContext> ContextAsync(CancellationToken ct)
    {
        if (_context is not null)
        {
            return _context;
        }

        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = await callers.ResolveAsync(state.User, ct).ConfigureAwait(false);
        _context = principal?.Context ?? AlvoContext.Anonymous;
        return _context;
    }

    /// <summary>One page of records.</summary>
    public async Task<AlvoPage> PageAsync(
        string entity, int limit, string? after, bool total, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        return await data.QueryAsync(
            new AlvoQuery { Entity = entity, Limit = limit, After = after, IncludeTotalCount = total },
            context,
            ct).ConfigureAwait(false);
    }

    /// <summary>One record, or nothing.</summary>
    /// <remarks>
    /// <b>Nothing is the honest answer for two different situations</b> and the screen must not
    /// merge them: the row does not exist, or it exists and this caller's read predicate excludes
    /// it. The Data API answers <c>404</c> to both on purpose — telling them apart would let a
    /// caller enumerate rows they may not read — so the dashboard says the same thing.
    /// </remarks>
    public async Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        return await data.GetAsync(entity, id, context, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a record.</summary>
    public async Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        return await data.CreateAsync(entity, values, context, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Changes the named fields of a record and leaves the rest alone.</summary>
    public async Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        return await data.UpdateAsync(entity, id, values, context, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a record.</summary>
    public async Task DeleteAsync(string entity, Guid id, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        await data.DeleteAsync(entity, id, context, cancellationToken: ct).ConfigureAwait(false);
    }
}
