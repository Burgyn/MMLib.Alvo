using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// What each command's access rule answers for a simulated caller — asked of <see cref="CommandAccessJudge"/>, the judge the
/// command filter and <c>IAlvoCommands</c> ask, so the simulator cannot answer otherwise than production (Ruling 33). A rule
/// that reads the row has no answer without one, and the simulator reads no row (Management deviation D4).
/// </summary>
internal sealed class CommandSimulator(ExtensionRegistry registry, CommandAccessJudge judge)
{
    /// <summary>The verdicts of the commands bound to <paramref name="entity"/> and of those bound to none.</summary>
    /// <param name="entity">The simulated entity.</param>
    /// <param name="caller">The simulated caller.</param>
    internal IReadOnlyList<ManagementCommandVerdict> Simulate(string entity, AlvoContext caller) =>
        [.. registry.Commands
            .Where(command => command.Entity is null || string.Equals(command.Entity, entity, StringComparison.Ordinal))
            .OrderBy(command => command.Sequence)
            .Select(command => new ManagementCommandVerdict
            {
                Name = command.Id,
                Entity = command.Entity,
                Access = command.Access.Source,
                Allowed = judge.Preview(command.Id, caller),
            })];
}
