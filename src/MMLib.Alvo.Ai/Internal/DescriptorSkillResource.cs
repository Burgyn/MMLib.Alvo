using Microsoft.Agents.AI;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>A fixed text a <see cref="DescriptorSkill"/> serves through <c>read_skill_resource</c>.</summary>
/// <remarks>
/// The framework's own inline resource is internal to it, so the skill that replaces its inline skill (D37) brings its
/// own. It returns the text as it is: nothing about a schema slice needs escaping to reach the model.
/// </remarks>
/// <param name="name">The name the model passes, exactly as the skill lists it.</param>
/// <param name="value">The text returned.</param>
/// <param name="description">What the resource is, as the skill lists it.</param>
internal sealed class DescriptorSkillResource(string name, string value, string description) : AgentSkillResource(name, description)
{
    public override Task<object?> ReadAsync(IServiceProvider? serviceProvider = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<object?>(value);
}
