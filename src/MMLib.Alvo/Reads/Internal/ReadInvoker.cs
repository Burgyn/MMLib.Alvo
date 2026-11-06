using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// How one read extension is called: a class built per read from the read's scope — or resolved, when registered — or a
/// delegate that takes no service. A class's constructor parameters are kept for the boot ban.
/// </summary>
internal sealed class ReadInvoker
{
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private ReadInvoker(
        string? typeName,
        IReadOnlyList<(string Name, Type Type)> dependencies,
        Func<IServiceProvider, QueryNarrowing, AlvoFilter?>? narrow,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        TypeName = typeName;
        Dependencies = dependencies;
        Narrow = narrow;
        _unresolvable = unresolvable;
    }

    /// <summary>Gets the extension class's full name; <see langword="null"/> for a delegate.</summary>
    internal string? TypeName { get; }

    internal bool IsClass => TypeName is not null;

    /// <summary>Gets every constructor parameter of a class extension; a delegate has none.</summary>
    internal IReadOnlyList<(string Name, Type Type)> Dependencies { get; }

    /// <summary>Gets the narrowing call, over the read's services; <see langword="null"/> for an enricher.</summary>
    internal Func<IServiceProvider, QueryNarrowing, AlvoFilter?>? Narrow { get; }

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static ReadInvoker NarrowingClass<TNarrowing>(Func<TNarrowing, QueryNarrowing, AlvoFilter?> call)
        where TNarrowing : class
    {
        if (typeof(TNarrowing).IsAbstract)
        {
            throw new ArgumentException(
                $"{typeof(TNarrowing).Name} is abstract, so Alvo cannot build it for a read; register a concrete narrowing class.", nameof(TNarrowing));
        }

        return new(
            typeof(TNarrowing).FullName,
            ConstructorParameters(typeof(TNarrowing)),
            (services, query) => InvokeClass(services, query, call),
            isService => HandlerInvoker.ClassUnresolvable(typeof(TNarrowing), isService));
    }

    internal static ReadInvoker NarrowingDelegate(Func<QueryNarrowing, AlvoFilter?> narrow)
    {
        ArgumentNullException.ThrowIfNull(narrow);
        return new(typeName: null, [], (_, query) => narrow(query), _ => []);
    }

    private static (string Name, Type Type)[] ConstructorParameters(Type type) =>
        [.. type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => (parameter.Name ?? "?", parameter.ParameterType))
            .Distinct()];

    private static TResult InvokeClass<TExtension, TResult>(
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
            if (registered is null && extension is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
