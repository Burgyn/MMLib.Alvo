using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Puts <see cref="AlvoIdentityDbContext.SchemaPrefix"/> into EF's model cache key, so two prefixes in one
/// process get two models.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this, taking the prefix from <see cref="AlvoOptions"/> is worse than the constant it
/// replaces.</b> EF's default <c>ModelCacheKey</c> is the context <em>type</em> plus the design-time flag,
/// and the built model is cached in EF's internal service provider — which is shared process-wide by every
/// container configured the same way. Two hosts, one under <c>alvo</c> and one under <c>acme</c>, would
/// therefore both be served whichever model was built first, and the second host would silently read and
/// write the first one's tables.
/// </para>
/// <para>
/// <b>A cache key rather than a context type per prefix.</b> A generated or subclassed context per prefix
/// would also work, and would cost a type the store registration, ASP.NET Core Identity's
/// <c>AddEntityFrameworkStores</c> and every internal consumer would have to name. The key is the smaller
/// change and says the same thing: the model depends on the prefix.
/// </para>
/// </remarks>
internal sealed class AlvoIdentityModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc/>
    public object Create(DbContext context, bool designTime) =>
        (context?.GetType(), (context as AlvoIdentityDbContext)?.SchemaPrefix, designTime);
}
