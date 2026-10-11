using MMLib.Alvo.DocsGen.Llms;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Repo;

public partial class AgentSkillsInstallerTests
{
    private static readonly string _root = RepositoryRoot.Find();

    [Fact]
    public void The_installer_names_every_descriptor_skill()
    {
        var script = File.ReadAllText(Path.Combine(_root, "scripts", "install-agent-skills"));
        var listed = SkillList().Match(script);
        listed.Success.ShouldBeTrue("scripts/install-agent-skills needs its SKILLS=\"...\" list");

        var named = listed.Groups["list"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actual = Directory.GetDirectories(Path.Combine(_root, "plugins", "alvo", "skills"), "alvo-descriptor-*")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        named.Order(StringComparer.Ordinal).ShouldBe(actual, "a new alvo-descriptor-* skill must be added to scripts/install-agent-skills");
    }

    [Fact]
    public void The_site_and_llms_txt_show_the_same_one_liner()
    {
        var snippet = File.ReadAllText(Path.Combine(_root, "website", "src", "snippets", "shell", "install-agent-skills.sh")).Trim();

        snippet.ShouldBe(LlmsWriter.InstallSkills);
        LlmsWriter.Index([], [("alvo-descriptor-hooks", "H.")]).ShouldContain($"`{snippet}`");
    }

    [Fact]
    public void The_plugin_commands_the_docs_show_name_the_repository_marketplace()
    {
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, ".claude-plugin", "marketplace.json")))!;
        var entry = marketplace["plugins"]!.AsArray().Single()!;
        var source = entry["source"]!.GetValue<string>();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, source, ".claude-plugin", "plugin.json")))!;
        var id = $"{entry["name"]}@{marketplace["name"]}";

        manifest["name"]!.GetValue<string>().ShouldBe(entry["name"]!.GetValue<string>());
        source.ShouldBe("./plugins/alvo", "the descriptor skills live in plugins/alvo/skills; MMLib.Alvo.Ai embeds them from there");
        foreach (var snippet in new[] { "claude-plugin.txt", "claude-plugin-install.sh" })
        {
            var text = File.ReadAllText(Path.Combine(_root, "website", "src", "snippets", "shell", snippet));
            text.ShouldContain("marketplace add Burgyn/MMLib.Alvo");
            text.ShouldContain($"install {id}");
        }
    }

    [GeneratedRegex("^SKILLS=\"(?<list>[^\"]*)\"", RegexOptions.Multiline)]
    private static partial Regex SkillList();
}
