using Microsoft.Agents.AI;

using System.Security;
using System.Text;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>One descriptor skill: its frontmatter, its body as <c>load_skill</c> returns it, and its resources.</summary>
/// <remarks>
/// <para>
/// <b>Not <see cref="AgentInlineSkill"/>, deliberately (D37).</b> 1.22.0's inline skill XML-escapes its body, so
/// <c>'admin' in @user.roles</c> reaches the model as <c>&amp;apos;admin&amp;apos; in @user.roles</c> and
/// <c>{"fields": …}</c> as <c>{&amp;quot;fields&amp;quot;: …}</c>. A model that has to reproduce CEL and JSON from
/// entity-escaped text writes the entities back, the <see cref="ToolJson"/> failure in another encoding. This skill
/// emits the same envelope, <c>&lt;name&gt;</c>, <c>&lt;description&gt;</c>, <c>&lt;instructions&gt;</c> and
/// <c>&lt;available_resources&gt;</c>, with the text left as written.
/// </para>
/// <para>
/// <b>HTML comments are removed from what the model reads</b> (<see cref="SkillMarkdown.WithoutComments"/>). The
/// resource list is the one place text is escaped: a name and a description sit inside XML attributes, and the
/// citation grammar already keeps quotes and angle brackets out of a name, so escaping it changes nothing the model
/// has to type back.
/// </para>
/// <para>
/// No scripts: <see cref="AgentSkill.GetScriptAsync"/> keeps its default, and the script tool is not offered (D28).
/// </para>
/// </remarks>
internal sealed class DescriptorSkill : AgentSkill
{
    private readonly string _content;
    private readonly IReadOnlyList<DescriptorSkillResource> _resources;

    /// <summary>Initializes the skill; the frontmatter is validated by the framework's own rules, and throws.</summary>
    internal DescriptorSkill(SkillParts parts, IReadOnlyList<DescriptorSkillResource> resources)
    {
        Frontmatter = new AgentSkillFrontmatter(parts.Name, parts.Description);
        _resources = resources;
        _content = Envelope(parts, resources);
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_content);

    public override ValueTask<AgentSkillResource?> GetResourceAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSkillResource?>(_resources.FirstOrDefault(resource => resource.Name == name));

    private static string Envelope(SkillParts parts, IReadOnlyList<DescriptorSkillResource> resources) =>
        new StringBuilder()
            .Append("<name>").Append(parts.Name).Append("</name>\n")
            .Append("<description>").Append(parts.Description).Append("</description>\n\n")
            .Append("<instructions>\n").Append(SkillMarkdown.WithoutComments(parts.Body).TrimEnd('\n')).Append("\n</instructions>\n\n")
            .Append(ResourceList(resources))
            .ToString();

    private static string ResourceList(IReadOnlyList<DescriptorSkillResource> resources) =>
        resources.Count == 0
            ? "<available_resources />"
            : $"<available_resources>\n{string.Concat(resources.Select(ResourceLine))}</available_resources>";

    private static string ResourceLine(DescriptorSkillResource resource) =>
        $"  <resource name=\"{SecurityElement.Escape(resource.Name)}\" description=\"{SecurityElement.Escape(resource.Description)}\"/>\n";
}
