using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Builds one <see cref="AlvoDataContext"/> per data operation and mints a fresh model token whenever
/// the applied <see cref="SchemaModel"/> changes. A context per operation rather than a scoped shared
/// one: the data path opens its own transaction for a write and never hands a context out, so there is
/// nothing to share, and a long-lived context would keep a change tracker alive next to a code path
/// whose whole design is that no change tracker exists.
/// </summary>
internal sealed class AlvoDataContextFactory
{
    private readonly ISchemaRegistry _schemas;
    private readonly Action<DbContextOptionsBuilder> _configureProvider;
    private readonly Lock _gate = new();
    private SchemaModel? _observed;
    private Guid _token;

    internal AlvoDataContextFactory(ISchemaRegistry schemas, Action<DbContextOptionsBuilder> configureProvider)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(configureProvider);
        _schemas = schemas;
        _configureProvider = configureProvider;
    }

    internal AlvoDataContext Create()
    {
        var schema = _schemas.GetSchema();
        var token = TokenFor(schema);
        var options = new DbContextOptionsBuilder();
        _configureProvider(options);
        options.ReplaceService<IModelCacheKeyFactory, AlvoModelCacheKeyFactory>();

        return new AlvoDataContext(options.Options, schema, token);
    }

    /// <summary>
    /// The token for <paramref name="schema"/>: a new <see cref="Guid"/> the first time a given applied
    /// model instance is seen, and the same one thereafter. Keyed on reference identity rather than on a
    /// content hash because the applied model is replaced wholesale on every apply — a new object is
    /// exactly the signal a new model is needed, and a deep hash of every entity and field on every
    /// operation would not be.
    /// </summary>
    private Guid TokenFor(SchemaModel schema)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_observed, schema))
            {
                _observed = schema;
                _token = Guid.NewGuid();
            }

            return _token;
        }
    }
}
