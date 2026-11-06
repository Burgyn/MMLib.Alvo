using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// How one read extension is called: a class built per read from the read's scope — or resolved, when registered — and
/// disposed after the call, asynchronously when it can be, or a delegate that takes no service. A class's constructor parameters are kept for the boot ban.
/// </summary>
internal sealed class ReadInvoker
{
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private ReadInvoker(
        string? typeName,
        IReadOnlyList<(string Name, Type Type)> dependencies,
        Func<IServiceProvider, QueryNarrowing, ValueTask<AlvoFilter?>>? narrow,
        Func<IServiceProvider, EnricherCall>? enricher,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        TypeName = typeName;
        Dependencies = dependencies;
        Narrow = narrow;
        Enricher = enricher;
        _unresolvable = unresolvable;
    }

    /// <summary>Gets the extension class's full name; <see langword="null"/> for a delegate.</summary>
    internal string? TypeName { get; }

    internal bool IsClass => TypeName is not null;

    /// <summary>Gets every constructor parameter of a class extension; a delegate has none.</summary>
    internal IReadOnlyList<(string Name, Type Type)> Dependencies { get; }

    /// <summary>Gets the narrowing call, over the read's services; <see langword="null"/> for an enricher.</summary>
    internal Func<IServiceProvider, QueryNarrowing, ValueTask<AlvoFilter?>>? Narrow { get; }

    /// <summary>
    /// Gets what builds an enricher for one read, over the read's services; <see langword="null"/> for a narrowing. A class is
    /// built once per read and handed to every row of it.
    /// </summary>
    internal Func<IServiceProvider, EnricherCall>? Enricher { get; }

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static ReadInvoker NarrowingClass<TNarrowing>(Func<TNarrowing, QueryNarrowing, AlvoFilter?> call)
        where TNarrowing : class
    {
        EnsureConcrete<TNarrowing>("narrowing");

        return new(
            typeof(TNarrowing).FullName,
            ConstructorParameters(typeof(TNarrowing)),
            (services, query) => InvokeClassAsync(services, query, call),
            enricher: null,
            isService => HandlerInvoker.ClassUnresolvable(typeof(TNarrowing), isService));
    }

    internal static ReadInvoker NarrowingDelegate(Func<QueryNarrowing, AlvoFilter?> narrow)
    {
        ArgumentNullException.ThrowIfNull(narrow);
        return new(typeName: null, [], (_, query) => ValueTask.FromResult(narrow(query)), enricher: null, _ => []);
    }

    internal static ReadInvoker EnricherClass<TEnricher>(Action<TEnricher, RowShaping, AlvoContext, DataOperation> call)
        where TEnricher : class
    {
        EnsureConcrete<TEnricher>("enricher");

        return new(
            typeof(TEnricher).FullName,
            ConstructorParameters(typeof(TEnricher)),
            narrow: null,
            services => BuildClass(services, call),
            isService => HandlerInvoker.ClassUnresolvable(typeof(TEnricher), isService));
    }

    internal static ReadInvoker EnricherDelegate(Action<RowShaping, AlvoContext, DataOperation> enrich)
    {
        ArgumentNullException.ThrowIfNull(enrich);
        var call = new EnricherCall(enrich, Owned: null);
        return new(typeName: null, [], narrow: null, _ => call, _ => []);
    }

    private static void EnsureConcrete<TExtension>(string noun)
    {
        if (typeof(TExtension).IsAbstract)
        {
            throw new ArgumentException(
                $"{typeof(TExtension).Name} is abstract, so Alvo cannot build it for a read; register a concrete {noun} class.", nameof(TExtension));
        }
    }

    private static EnricherCall BuildClass<TEnricher>(IServiceProvider services, Action<TEnricher, RowShaping, AlvoContext, DataOperation> call)
        where TEnricher : class
    {
        var registered = services.GetService<TEnricher>();
        var enricher = registered ?? ActivatorUtilities.CreateInstance<TEnricher>(services);

        return new((row, caller, operation) => call(enricher, row, caller, operation), registered is null ? enricher : null);
    }

    private static (string Name, Type Type)[] ConstructorParameters(Type type) =>
        [.. type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => (parameter.Name ?? "?", parameter.ParameterType))
            .Distinct()];

    private static async ValueTask<TResult> InvokeClassAsync<TExtension, TResult>(
        IServiceProvider services, QueryNarrowing query, Func<TExtension, QueryNarrowing, TResult> call)
        where TExtension : class
    {
        var registered = services.GetService<TExtension>();
        var extension = registered ?? ActivatorUtilities.CreateInstance<TExtension>(services);
        try
        {
            return call(extension, query);
        }
        finally
        {
            if (registered is null)
            {
                await InvokerCalls.DisposeAsync(extension).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>One enricher, built for one read: the call each row goes through, and the instance the read owns and disposes.</summary>
/// <param name="Call">The row, the caller and the operation, handed to the enricher.</param>
/// <param name="Owned">The class instance Alvo built, not the container; <see langword="null"/> for a delegate or a registered service.</param>
internal sealed record EnricherCall(Action<RowShaping, AlvoContext, DataOperation> Call, object? Owned);
