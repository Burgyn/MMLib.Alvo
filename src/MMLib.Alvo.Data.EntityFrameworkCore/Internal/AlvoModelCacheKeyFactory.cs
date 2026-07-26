using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Keys EF's model cache on the applied schema as well as the context type. EF caches exactly one model
/// per <see cref="DbContext"/> CLR type, and Alvo's model is built from a descriptor that changes at
/// runtime — without this, the first schema a process ever saw would be served forever, so a field added
/// by a runtime apply would be invisible and a removed one would still be queried.
/// </summary>
internal sealed class AlvoModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc/>
    /// <remarks>
    /// Another context type reaching this factory is refused rather than defaulted. A shared fallback token
    /// would make two context types silently share one cached model, which is the exact bug this factory
    /// exists to prevent — and since <see cref="AlvoDataContext"/> installs the factory itself, no other
    /// type can arrive here without someone having wired it in deliberately.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="context"/> is not an <see cref="AlvoDataContext"/>.</exception>
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context is not AlvoDataContext alvo)
        {
            throw new ArgumentException(
                $"'{context.GetType()}' is not an {nameof(AlvoDataContext)}, so it has no applied-schema token to key "
                + "its model cache on.",
                nameof(context));
        }

        return (context.GetType(), alvo.ModelToken, designTime);
    }
}
