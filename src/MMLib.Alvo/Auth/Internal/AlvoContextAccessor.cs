namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The default <see cref="IAlvoContextAccessor"/>: an <see cref="AsyncLocal{T}"/> holder, the same
/// mechanism ASP.NET Core's own <c>HttpContextAccessor</c> uses, and deliberately <b>not</b> an
/// <c>HttpContext</c> lookup — it lives in the ASP.NET-free auth feature so the port stays implementable
/// by a host that is not an ASP.NET application at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is: a per-request ambient convenience, last writer wins, nesting not supported.</b> One
/// writer publishes a caller for the duration of one request (the Data API's
/// <c>AlvoContextFilter</c>) and clears it on the way out. Publishing while another caller is already
/// published <em>replaces</em> it and does not restore the outer one afterwards. Nothing in the framework
/// nests today, and adding a push/pop scope for a case that does not exist would be an unreachable
/// control — the defect class this PR keeps closing. If nesting ever arrives, the scope arrives with the
/// caller that needs it, and with a fact that exercises it.
/// </para>
/// <para>
/// <b>What this is not: how a post-commit path learns who it is acting as.</b> The outbox dispatcher,
/// after-hooks and automation actions run with <em>no request in flight</em>, so there is nothing ambient
/// for them to read — which is precisely why <c>IAlvoData</c> takes <see cref="AlvoContext"/> as a
/// required parameter on every member and those paths pass one explicitly (frequently
/// <see cref="AlvoContext.System"/>). This accessor is for code that wants to <em>observe</em> the current
/// request's caller — logging, a host's own endpoint sitting beside Alvo's — never for enforcement, and
/// nothing in the data path reads it.
/// </para>
/// <para>
/// Outside a request it reads <see langword="null"/>, and that means "no caller was published", never
/// "anonymous": an anonymous caller is <see cref="AlvoContext.Anonymous"/>, and the Data API publishes no
/// principal for one, because there is no key for a principal to describe.
/// </para>
/// <para>
/// The value is held behind a mutable box rather than stored in the <see cref="AsyncLocal{T}"/>
/// directly. Assigning <see langword="null"/> to an <see cref="AsyncLocal{T}"/> only clears it for the
/// <em>current</em> execution context, so a value set inside a request and cleared on the way out would
/// stay visible to any flow that captured the context in between; clearing the box clears it for every
/// holder of that same box at once. This is the identical reasoning behind
/// <c>HttpContextAccessor</c>'s <c>HttpContextHolder</c>.
/// </para>
/// <para>
/// Registered as a singleton: the instance is stateless and the per-request state lives in the ambient
/// execution context, not in the object.
/// </para>
/// </remarks>
internal sealed class AlvoContextAccessor : IAlvoContextAccessor
{
    private static readonly AsyncLocal<PrincipalHolder> _current = new();

    /// <inheritdoc/>
    public AlvoPrincipal? Principal
    {
        get => _current.Value?.Principal;
        set
        {
            if (_current.Value is { } holder)
            {
                // Clear the captured box first, so an execution context that captured it before this
                // assignment stops seeing the previous caller.
                holder.Principal = null;
            }

            if (value is not null)
            {
                _current.Value = new PrincipalHolder { Principal = value };
            }
        }
    }

    private sealed class PrincipalHolder
    {
        public AlvoPrincipal? Principal { get; set; }
    }
}
