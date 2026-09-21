namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The package's <see cref="IAlvoBootstrapAdmin"/>: who, if anyone, the deployment configured as its
/// one bootstrap administrator.
/// </summary>
/// <remarks>
/// <b>Written once, read everywhere.</b> <see cref="AlvoIdentityBootstrap"/> publishes the seeded
/// identifier during <c>StartAsync</c> — before the server accepts a request — and every management
/// call reads it afterwards, so the value is held in a <see cref="Volatile"/> field rather than behind
/// a lock. It is held through a holder object because <see cref="UserId"/> wraps a <see cref="Guid"/>:
/// a 16-byte struct has no atomic read, and <see cref="Volatile"/> has no overload for one, so a reader
/// racing the single write could otherwise observe half of each. Nothing is published until an
/// administrator exists, which is what makes "no identity" the default answer.
/// </remarks>
internal sealed class AlvoBootstrapAdmin : IAlvoBootstrapAdmin
{
    private Seeded? _admin;

    /// <inheritdoc/>
    public bool IsBootstrapAdmin(UserId user) =>
        Volatile.Read(ref _admin) is { Admin: var admin } && admin == user;

    /// <summary>Publishes the seeded administrator's identifier.</summary>
    /// <param name="admin">The administrator's identifier; never the reserved all-zero value.</param>
    /// <exception cref="ArgumentException"><paramref name="admin"/> is the reserved all-zero value.</exception>
    internal void Publish(UserId admin)
    {
        if (admin == default)
        {
            throw new ArgumentException(
                "The all-zero UserId means 'no identity' and can never be the bootstrap administrator.",
                nameof(admin));
        }

        Volatile.Write(ref _admin, new Seeded(admin));
    }

    /// <summary>The published administrator, or the absence of one when the reference is null.</summary>
    /// <param name="Admin">The seeded administrator's identifier.</param>
    private sealed record Seeded(UserId Admin);
}
