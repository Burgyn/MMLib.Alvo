using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>Problems that span commands: one operationId per command, and handlers of completions no command produces.</summary>
internal static class CommandProblems
{
    /// <summary>Whether the composition can run commands: a command ledger and transaction units are registered.</summary>
    /// <param name="services">The composition's service lookup.</param>
    internal static bool Relational(IServiceProviderIsService services) =>
        services.IsService(typeof(ICommandLedger)) && services.IsService(typeof(IAlvoTransactions));

    /// <summary>Every problem between registrations.</summary>
    /// <param name="registry">The host's registrations.</param>
    internal static IReadOnlyList<ExtensionProblem> Shared(ExtensionRegistry registry) =>
        [.. OperationIdCollisions(registry), .. CompletionsOfNoCommand(registry)];

    private static IEnumerable<ExtensionProblem> OperationIdCollisions(ExtensionRegistry registry) =>
        registry.Commands.Where(command => command.IsMapped && ExtensionIds.IsCommandName(command.Id))
            .GroupBy(command => CommandOperationIds.Of(command.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(command => new ExtensionProblem(
                command.Id,
                CommandBinding.Kind,
                $"extensions/{command.Id}/id",
                $"'{string.Join("' and '", group.Select(other => other.Id))}' publish one OpenAPI operationId, '{group.Key}', so a client generated from the document could not tell them apart.",
                "Rename one of them.")));

    private static IEnumerable<ExtensionProblem> CompletionsOfNoCommand(ExtensionRegistry registry)
    {
        var completions = registry.Commands.Select(command => command.EventType).ToHashSet(StringComparer.Ordinal);

        return registry.Handlers
            .Where(handler => handler.Source is NamedEvent { Type: var type }
                && type.StartsWith(CommandRegistration.EventPrefix, StringComparison.Ordinal)
                && !completions.Contains(type))
            .Select(handler => new ExtensionProblem(
                handler.Id,
                ExtensionBindingCompiler.HandlerKind,
                $"extensions/{handler.Id}/event",
                $"It handles '{((NamedEvent)handler.Source).Type}', but this host registers no command that completes with it, so it would never run.",
                "Name a command this host maps or adds — alvo.command.<name>.completed — or remove the handler."));
    }
}
