namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The default <see cref="IAlvoContextAccessor"/>: an <see cref="AsyncLocal{T}"/> holder, the same
/// mechanism ASP.NET Core's own <c>HttpContextAccessor</c> uses, and deliberately <b>not</b> an
/// <c>HttpContext</c> lookup — the accessor lives in the ASP.NET-free auth feature because an embedded
/// host, a background job and the outbox dispatcher all want to publish and read a caller without an
/// HTTP request in flight.
/// </summary>
/// <remarks>
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
