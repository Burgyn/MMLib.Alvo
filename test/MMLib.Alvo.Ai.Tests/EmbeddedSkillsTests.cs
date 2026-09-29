using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The descriptor skills as the package embeds and serves them (D23, D26, D28, D33, D37).</summary>
public sealed class EmbeddedSkillsTests
{
    private const string Indexes = "alvo-descriptor-indexes";
    private const string IndexesPointer = "/$defs/entity/properties/indexes";
    private const string RoleCheck = "'admin' in @user.roles && size(x) < 3 & \"q\"";

    private static readonly string[] _readTools = [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_embedded_file_sits_under_a_descriptor_skill_directory_with_forward_slashes() =>
        EmbeddedSkills.Files.Keys.ShouldAllBe(path =>
            path.StartsWith("alvo-descriptor-", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains('\r'));

    [Fact]
    public void Every_embedded_text_is_line_feed_normalised() =>
        EmbeddedSkills.Files.Values.ShouldAllBe(text => !text.Contains('\r'));

    [Fact]
    public void Every_skill_file_is_served_as_a_skill_of_its_directorys_name() =>
        EmbeddedSkills.All.Select(skill => skill.Frontmatter.Name + "/SKILL.md")
            .ShouldBe(EmbeddedSkills.Files.Keys.Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

    [Fact]
    public async Task A_cited_schema_pointer_is_served_under_its_citation_as_that_slice_of_the_schema()
    {
        var skill = EmbeddedSkills.All.Single(candidate => candidate.Frontmatter.Name == Indexes);

        var resource = (await skill.GetResourceAsync(EmbeddedSkills.SchemaReference + IndexesPointer, Ct)).ShouldNotBeNull();
        var served = (string)(await resource.ReadAsync(cancellationToken: Ct))!;

        JsonNode.DeepEquals(JsonNode.Parse(served), Schema()["$defs"]!["entity"]!["properties"]!["indexes"]).ShouldBeTrue();
    }

    [Fact]
    public void A_pointer_the_schema_does_not_have_is_a_loud_failure() =>
        Should.Throw<InvalidOperationException>(() => EmbeddedSkills.Slice("/$defs/no-such-definition"))
            .Message.ShouldContain(EmbeddedSkills.SchemaReference + "/$defs/no-such-definition");

    [Fact]
    public void A_body_cites_each_schema_pointer_once() =>
        EmbeddedSkills.SchemaPointers("See `schema/project.schema.json#/$defs/a` and (schema/project.schema.json#/$defs/b), then schema/project.schema.json#/$defs/a.")
            .ShouldBe(["/$defs/a", "/$defs/b"]);

    [Fact]
    public void A_frontmatter_that_is_not_name_then_description_is_refused() =>
        Should.Throw<InvalidOperationException>(() => SkillMarkdown.Parse("---\ndescription: d\nname: n\n---\nbody"));

    [Fact]
    public void A_skill_file_splits_into_its_name_description_and_body() =>
        SkillMarkdown.Parse("---\nname: alvo-descriptor-x\ndescription: Use when x.\n---\n\n# X\n")
            .ShouldBe(new SkillParts("alvo-descriptor-x", "Use when x.", "# X\n"));

    [Fact]
    public async Task A_loaded_skill_carries_its_body_verbatim_without_its_comments()
    {
        var skill = new DescriptorSkill(
            new SkillParts("alvo-descriptor-x", "Use when x.", $"Rule:\n<!-- gen:roles -->\n`{RoleCheck}`\n<!-- /gen:roles -->\nEnd.\n"),
            [new DescriptorSkillResource("schema/project.schema.json#/$defs/x", "{}", "A slice.")]);

        var content = await skill.GetContentAsync(Ct);

        content.ShouldContain($"<instructions>\nRule:\n`{RoleCheck}`\nEnd.\n</instructions>", Case.Sensitive);
        content.ShouldNotContain("<!--");
        content.ShouldContain("<resource name=\"schema/project.schema.json#/$defs/x\" description=\"A slice.\"/>");
    }

    [Fact]
    public async Task A_skill_without_resources_says_so_and_offers_no_scripts()
    {
        var content = await new DescriptorSkill(new SkillParts("alvo-descriptor-x", "Use when x.", "Body.\n"), []).GetContentAsync(Ct);

        content.ShouldBe("<name>alvo-descriptor-x</name>\n<description>Use when x.</description>\n\n<instructions>\nBody.\n</instructions>\n\n<available_resources />");
    }

    [Fact]
    public async Task The_model_sees_both_read_tools_without_approval_and_never_the_script_tool()
    {
        var model = new ScriptedChatClient(Scripted.Says("ok"));

        await DrainAsync(model);

        var tools = model.Options[0].ShouldNotBeNull().Tools.ShouldNotBeNull();
        _readTools.ShouldBeSubsetOf(tools.Select(tool => tool.Name));
        tools.Select(tool => tool.Name).ShouldNotContain(AgentSkillsProvider.RunSkillScriptToolName);
        tools.ShouldAllBe(tool => !(tool is ApprovalRequiredAIFunction));
    }

    [Fact]
    public async Task The_instructions_the_model_receives_end_with_the_skill_list()
    {
        var model = new ScriptedChatClient(Scripted.Says("ok"));

        await DrainAsync(model);

        var instructions = model.Options[0].ShouldNotBeNull().Instructions.ShouldNotBeNull();
        instructions.ShouldStartWith(AssistantInstructions.Text);
        EmbeddedSkills.All.ShouldAllBe(skill => instructions.Contains($"<name>{skill.Frontmatter.Name}</name>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_that_loads_a_skill_goes_on_without_an_approval_halt()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = Indexes }),
            Scripted.Says("Loaded."));

        var updates = await DrainAsync(model);

        string.Concat(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text))
            .ShouldContain("Loaded.");
        updates.SelectMany(update => update.Contents).OfType<ToolApprovalRequestContent>().ShouldBeEmpty();
        LoadedSkills(model).ShouldContain(result => result.Contains("<instructions>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_loaded_skill_reaches_the_model_unescaped()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = Indexes }),
            Scripted.Says("Loaded."));

        await DrainAsync(model);

        LoadedSkills(model).ShouldContain(result => result.Contains("\"unique\": true", StringComparison.Ordinal) && !result.Contains("&quot;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_that_loops_on_load_skill_is_cut_off_at_the_iteration_cap()
    {
        var looping = Enumerable.Range(0, 30)
            .Select(_ => Scripted.Calls(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = Indexes })).ToArray();
        var model = new ScriptedChatClient(looping);

        await DrainAsync(model);

        model.Options.Count.ShouldBe(AlvoAssistant.MaximumIterations + 1);
        LoadedSkills(model).ShouldContain(result => result.Contains("<instructions>", StringComparison.Ordinal));
    }

    private static IEnumerable<string> LoadedSkills(ScriptedChatClient model) =>
        model.Sent.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result!.ToString()!);

    private static async Task<List<AgentResponseUpdate>> DrainAsync(ScriptedChatClient model)
    {
        var agent = AlvoAssistant.AgentFor(model, ManagementTools.For(Substitute.For<IAlvoManagement>(), "p"));
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")], session: null, options: null, Ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
}
