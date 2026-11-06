using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// The core's <see cref="IReadExtensionRunner"/>: the narrowings registered on an entity, in registration order, each asked
/// once per read and ANDed together — for every caller but the system, and never for a rules-only read. It fails closed: a
/// narrowing that is unbound, throws, cannot be translated, exceeds the filter limits or names a field the entity does not
/// declare fails the read, logged with its id and reason only.
/// </summary>
internal sealed class ReadExtensionRunner(
    ExtensionRegistry registry, ExtensionBinder binder, IPolicyCatalogProvider catalogs, IServiceScopeFactory scopes,
    ILogger<ReadExtensionRunner> logger) : IReadExtensionRunner
{
    private readonly ConcurrentDictionary<(string Extension, string Field), bool> _warned = new();

    public ReadNarrowing? Narrowing(string entity, DataOperation operation, AlvoContext context)
    {
        var registered = Narrowings(entity);
        if (registered.Count == 0 || context.ActorKind == AlvoActorKind.System || AlvoFlow.IsRulesOnlyRead)
        {
            return null;
        }

        var query = new QueryNarrowing(context, entity, operation);
        var scope = registered.Any(read => read.Invoker.IsClass) ? scopes.CreateScope() : null;
        try
        {
            return Combined(registered, Narrowed(registered, entity, scope?.ServiceProvider ?? EmptyServices.Instance, query));
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private List<ReadExtensionRegistration> Narrowings(string entity) =>
        [.. registry.ReadExtensions.Where(read => read.Kind == ReadExtensionKind.Narrowing && read.Covers(entity))];

    private static ReadNarrowing? Combined(List<ReadExtensionRegistration> registered, List<AlvoFilter> terms) =>
        terms.Count == 0
            ? null
            : new ReadNarrowing(terms.Count == 1 ? terms[0] : new AlvoAnd(terms), [.. registered.Select(read => read.Id)]);

    private List<AlvoFilter> Narrowed(
        List<ReadExtensionRegistration> registered, string entity, IServiceProvider services, QueryNarrowing query)
    {
        var (schema, policy, bindings) = Bound(entity);
        return [.. registered.Select(narrowing => Narrowed(narrowing, bindings, schema, policy, services, query)).OfType<AlvoFilter>()];
    }

    private (EntitySchema? Schema, EntityPolicy? Policy, ExtensionBindings Bindings) Bound(string entity)
    {
        if (catalogs.Current is not { } catalog)
        {
            return (null, null, ExtensionBindings.None);
        }

        var schema = catalog.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));
        return (schema, catalog.TryGetEntity(entity, out var policy) ? policy : null, binder.For(catalog));
    }

    private AlvoFilter? Narrowed(
        ReadExtensionRegistration narrowing, ExtensionBindings bindings, EntitySchema? schema, EntityPolicy? policy,
        IServiceProvider services, QueryNarrowing query)
    {
        if (schema is null || bindings.ReadExtension(narrowing.Id) is null)
        {
            throw Failed(narrowing.Id, HookFailedException.Unbound, cause: null);
        }

        var filter = Invoked(narrowing, services, query);
        return filter is null ? null : Checked(narrowing.Id, filter, schema, policy);
    }

    private AlvoFilter? Invoked(ReadExtensionRegistration narrowing, IServiceProvider services, QueryNarrowing query)
    {
        try
        {
            return narrowing.Invoker.Narrow!(services, query);
        }
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            throw Failed(narrowing.Id, cause is AlvoTranslationException ? HookFailedException.Translation : HookFailedException.Exception, cause);
        }
    }

    private AlvoFilter Checked(string id, AlvoFilter filter, EntitySchema schema, EntityPolicy? policy)
    {
        List<string> fields;
        try
        {
            AlvoFilter.EnsureWithinLimits(filter);
            fields = [.. AlvoFilter.ReferencedFields(filter)];
        }
        catch (ArgumentException cause)
        {
            throw Failed(id, HookFailedException.InvalidFilter, cause);
        }

        if (!fields.All(name => Filterable(name, schema)))
        {
            throw Failed(id, HookFailedException.InvalidFilter, cause: null);
        }

        WarnOfHidden(id, fields, policy);
        return filter;
    }

    private static bool Filterable(string name, EntitySchema schema) =>
        AlvoManagedColumns.For(schema).Contains(name)
        || schema.Fields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private void WarnOfHidden(string id, IEnumerable<string> fields, EntityPolicy? policy)
    {
        foreach (var field in fields.Where(field => policy?.FlaggedHidden.Contains(field) == true))
        {
            if (_warned.TryAdd((id, field), true))
            {
                ReadLog.NarrowingNamesHiddenField(logger, id, field);
            }
        }
    }

    private HookFailedException Failed(string id, string reason, Exception? cause)
    {
        ReadLog.ReadExtensionFailed(logger, id, reason, cause?.GetType().FullName ?? "-");
        var failure = HookFailedException.OfRead([id], reason);
        AlvoFlow.Unit?.Blame(failure);
        return failure;
    }

    /// <summary>The services a delegate narrowing is handed: none — it takes no service.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        internal static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
