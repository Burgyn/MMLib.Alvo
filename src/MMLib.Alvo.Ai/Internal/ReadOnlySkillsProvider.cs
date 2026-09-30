using Microsoft.Agents.AI;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>The framework's skills provider with <c>run_skill_script</c> taken out (D28).</summary>
/// <remarks>
/// <b>1.22.0 advertises the script tool whether or not any skill has a script</b>, and advertises it as
/// approval-required, so a model that called it would end the turn on an approval request the drawer cannot draw. No
/// descriptor skill has a script. The inner provider's instructions and its two read tools pass through unchanged,
/// and the agent's own invoker, cap and all, runs them. The inner provider's
/// <see cref="AIContextProvider.InvokingAsync"/> already merges the agent's instructions and tools with its own, so
/// its result is the whole context and is not merged a second time.
/// </remarks>
/// <param name="skills">The provider whose context is passed on without the script tool.</param>
internal sealed class ReadOnlySkillsProvider(AIContextProvider skills) : AIContextProvider
{
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var provided = await skills.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
        provided.Tools = provided.Tools?.Where(tool => tool.Name != AgentSkillsProvider.RunSkillScriptToolName).ToList();
        return provided;
    }

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default) =>
        skills.InvokedAsync(context, cancellationToken);
}
