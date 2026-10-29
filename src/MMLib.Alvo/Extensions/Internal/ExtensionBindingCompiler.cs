using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Checks every registration of this host and binds it to one primed catalog, collecting every problem at once (spec
/// 2026-10-14 §4.2). Two passes: <see cref="Check"/> needs no catalog — ids, services, options, the shape of what a
/// handler selects and a fieldless <c>when</c> — and <see cref="Compile"/> runs it again and then binds each named
/// entity: unknown entities, conditions that do not compile or read what the verb's event cannot answer.
/// </summary>
internal static class ExtensionBindingCompiler
{
    internal const string HandlerKind = "eventHandler";
    internal const string RetiredKind = "retired";

    /// <summary>The catalog-free pass: problems no descriptor can cure, and the ties among the handlers that pass it.</summary>
    internal static ExtensionRegistrationResult Check(
        ExtensionRegistry registry, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(ExtensionIdProblems.Of(registry));
        problems.AddRange(registry.Handlers.SelectMany(handler => new HandlerBinding(handler, compiler).CheckRegistration(services).Problems));
        var relational = CommandProblems.Relational(services);
        problems.AddRange(registry.Commands.SelectMany(command => new CommandBinding(command, compiler).CheckRegistration(relational).Problems));
        problems.AddRange(CommandProblems.Shared(registry));
        var refused = Refused(problems);

        return new(problems, Ties(registry.Handlers.Where(handler => !refused.Contains(handler.Id))));
    }

    /// <summary>Both passes against <paramref name="catalog"/>: what binds, what is left unbound, and why.</summary>
    internal static ExtensionBindingResult Compile(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(ExtensionIdProblems.Of(registry));
        var bound = BindHandlers(registry, catalog, compiler, services, problems);
        var commands = BindCommands(registry, catalog, compiler, services, problems);
        var refused = Refused(problems);
        List<BoundHandler> binds = [.. bound.Where(handler => !refused.Contains(handler.Id))];
        var bindings = new ExtensionBindings(
            binds,
            registry.Handlers.Where(handler => refused.Contains(handler.Id)),
            commands.Where(command => !refused.Contains(command.Name)));

        return new(bindings, problems, Ties(binds.Select(handler => handler.Registration)));
    }

    private static List<BoundHandler> BindHandlers(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services, List<ExtensionProblem> problems)
    {
        var bound = new List<BoundHandler>();
        foreach (var handler in registry.Handlers)
        {
            var binding = new HandlerBinding(handler, compiler).CheckRegistration(services);
            if (binding.Bind(catalog) is { } boundHandler)
            {
                bound.Add(boundHandler);
            }

            problems.AddRange(binding.Problems);
        }

        return bound;
    }

    private static List<BoundCommand> BindCommands(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services, List<ExtensionProblem> problems)
    {
        var relational = CommandProblems.Relational(services);
        var bound = new List<BoundCommand>();
        foreach (var command in registry.Commands)
        {
            var binding = new CommandBinding(command, compiler).CheckRegistration(relational);
            if (binding.Bind(catalog) is { } boundCommand)
            {
                bound.Add(boundCommand);
            }

            problems.AddRange(binding.Problems);
        }

        problems.AddRange(CommandProblems.Shared(registry));
        return bound;
    }

    private static HashSet<string> Refused(IEnumerable<ExtensionProblem> problems) =>
        problems.Select(problem => problem.Id).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<OrderTie> Ties(IEnumerable<HandlerRegistration> handlers) =>
        [.. handlers
            .SelectMany(handler => Targets(handler.Source).Select(target => (Target: target, Handler: handler)))
            .GroupBy(entry => (entry.Target, entry.Handler.Settings.Order))
            .Where(group => group.Count() > 1 && group.Any(entry => entry.Handler.Settings.OrderStated))
            .Select(group => new OrderTie(group.Key.Target, group.Key.Order, [.. group.Select(entry => entry.Handler.Id)]))];

    private static IEnumerable<string> Targets(HandlerSource source) => source switch
    {
        EntityEvents entities => entities.Entities.SelectMany(entity => Operations(entities.Verb).Select(operation => $"{entity} {operation}")),
        NamedEvent named => [named.Type],
        _ => [],
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["created"],
        HandlerVerb.Updated => ["updated"],
        HandlerVerb.Deleted => ["deleted"],
        _ => ["created", "updated"],
    };
}
