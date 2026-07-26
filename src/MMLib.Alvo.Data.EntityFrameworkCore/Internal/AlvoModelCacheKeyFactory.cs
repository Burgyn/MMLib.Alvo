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
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.GetType(), (context as AlvoDataContext)?.ModelToken ?? Guid.Empty, designTime);
    }
}
