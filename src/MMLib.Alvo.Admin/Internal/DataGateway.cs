using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

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
    /// <summary>The operator's own caller, as the Data API would resolve it.</summary>
    /// <remarks>
    /// <para>
    /// Exposed because two screens need to <em>say</em> what it is — the Data screen explains an
    /// empty page by naming the tenant the caller holds, and the Rules screen offers it as one of
    /// the callers to simulate. Neither of them decides anything with it.
    /// </para>
    /// <para>
    /// <b>Re-resolved on every call, never held.</b> A Blazor Server scope is the circuit, so a
    /// context resolved once would freeze the operator's roles and tenant for as long as the tab
    /// stays open — and it is the tenant that decides which rows the predicate admits. A revoked
    /// grant has to take effect on the next click, exactly as it does on the next HTTP request.
    /// </para>
    /// </remarks>
    public async ValueTask<AlvoContext> ContextAsync(CancellationToken ct)
    {
        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = await callers.ResolveAsync(state.User, ct).ConfigureAwait(false);
        return principal?.Context ?? AlvoContext.Anonymous;
    }

    /// <summary>One page of records, read as <paramref name="context"/>.</summary>
    /// <remarks>
    /// The caller is passed in rather than resolved here so a screen that reads a page and then its
    /// labels resolves the operator once, and both reads are made as the same caller.
    /// </remarks>
    public Task<AlvoPage> PageAsync(AlvoQuery query, AlvoContext context, CancellationToken ct)
        => data.QueryAsync(query, context, ct);

    /// <summary>The label of each row of <paramref name="entity"/> that <paramref name="values"/> point at.</summary>
    /// <remarks>
    /// An id with no entry is a row this caller cannot read, or one with no label; the screen draws
    /// both as the short id. A refusal — a label field masked from this caller, a scoped target and
    /// no tenant — is left to the caller, which knows what to fall back to.
    /// </remarks>
    /// <param name="entity">The target of the reference.</param>
    /// <param name="label">The target's label.</param>
    /// <param name="values">The reference values on the page, from every column pointing at this target.</param>
    /// <param name="context">The caller the page itself was read as.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(
        string entity, RowLabel label, IEnumerable<object?> values, AlvoContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        var labels = new Dictionary<Guid, string>();
        foreach (var batch in RefLabels.Batches(values))
        {
            var page = await data.QueryAsync(RefLabels.Query(entity, label, batch), context, ct)
                .ConfigureAwait(false);
            foreach (var row in page.Items)
            {
                if (RefLabels.IdOf(row[AlvoManagedColumns.Id]) is { } id && label.Of(row) is { } text)
                {
                    labels[id] = text;
                }
            }
        }

        return labels;
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
