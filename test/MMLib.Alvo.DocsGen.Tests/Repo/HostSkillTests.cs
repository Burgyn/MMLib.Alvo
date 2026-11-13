using MMLib.Alvo.DocsGen.Llms;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Repo;

/// <summary>
/// The developer-only <c>alvo-host-extensions</c> skill against what it names: the public API baselines, the docs pages
/// it links, and the compiled snippets its code is copied from.
/// </summary>
/// <remarks>
/// Not an <c>alvo-descriptor-*</c> skill, so <c>MMLib.Alvo.Ai</c> never embeds it and the assistant's conformance suite
/// never sees it: this suite is its only guard. It reads <c>plugins/</c>, <c>website/</c> and the
/// <c>PublicApi.*.verified.txt</c> baselines by path, so <c>scripts/ring0-scope</c> lists those baselines for it.
/// </remarks>
public sealed partial class HostSkillTests
{
    private const string Name = "alvo-host-extensions";
    private const int MaximumBytes = 8_192;
    private const int MaximumLines = 200;
    private const int MaximumDescription = 200;

    private static readonly string _root = RepositoryRoot.Find();

    private static readonly string _path = Path.Combine(_root, "plugins", "alvo", "skills", Name, "SKILL.md");

    private static readonly string _text = File.ReadAllText(_path).ReplaceLineEndings("\n");

    [Fact]
    public void The_skill_follows_the_descriptor_skills_frontmatter_standard()
    {
        var description = LlmsGenerator.DescriptionOf(_path);

        _text.ShouldStartWith($"---\nname: {Name}\ndescription: ");
        description.ShouldStartWith("Use when ");
        description.Length.ShouldBeLessThanOrEqualTo(MaximumDescription);
        description.IndexOfAny(['\'', '"']).ShouldBe(-1, "a description carries no quote or apostrophe");
    }

    /// <summary>The descriptor last: the skill says C# is the last resort before it names any API.</summary>
    [Fact]
    public void The_skill_puts_the_descriptor_first()
    {
        LlmsGenerator.DescriptionOf(_path).ShouldContain("only after the descriptor cannot express it");
        _text.IndexOf("## Rule 1: C# is the last resort", StringComparison.Ordinal)
            .ShouldBeLessThan(_text.IndexOf("## Registration", StringComparison.Ordinal));
    }

    [Fact]
    public void The_skill_keeps_its_size_caps()
    {
        Encoding.UTF8.GetByteCount(_text).ShouldBeLessThanOrEqualTo(MaximumBytes);
        _text.Count(character => character == '\n').ShouldBeLessThanOrEqualTo(MaximumLines);
    }

    /// <summary>The embed glob <c>alvo-descriptor-*</c> is the boundary that keeps host C# out of the assistant.</summary>
    [Fact]
    public void The_skill_is_outside_the_assistants_embed_glob()
    {
        var project = File.ReadAllText(Path.Combine(_root, "src", "MMLib.Alvo.Ai", "MMLib.Alvo.Ai.csproj"));

        project.ShouldContain("plugins/alvo/skills/alvo-descriptor-*/**/*.md");
        Name.ShouldNotStartWith("alvo-descriptor-");
    }

    [Fact]
    public void Every_api_name_the_skill_spells_in_code_is_in_a_public_api_baseline()
    {
        var baselines = string.Join('\n', Directory.GetFiles(Path.Combine(_root, "test"), "PublicApi.*.verified.txt", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        var names = CodeSpan().Matches(Fence().Replace(_text, string.Empty))
            .SelectMany(span => ApiName().Matches(span.Value).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        names.Count.ShouldBeGreaterThan(30, "the name scan matched almost nothing; the skill or the pattern moved");
        names.Where(name => !InBaselines(baselines, name)).ShouldBeEmpty("named in the skill, absent from every PublicApi baseline");
    }

    [Fact]
    public void Every_docs_page_the_skill_links_exists()
    {
        var slugs = DocsLink().Matches(_text).Select(match => match.Groups["slug"].Value).Distinct(StringComparer.Ordinal).ToList();
        var pages = Path.Combine(_root, "website", "src", "content", "docs", "extending");

        slugs.Count.ShouldBeGreaterThan(10);
        slugs.Where(slug => !File.Exists(Path.Combine(pages, slug + ".mdx")) && !File.Exists(Path.Combine(pages, slug + ".md")))
            .ShouldBeEmpty("linked from the skill, but no page under website/src/content/docs/extending/");
    }

    /// <summary>
    /// Code in the skill is copied, never written: every fence names its source, and each of its lines is a line of that
    /// file — compiled snippets under <c>website/src/snippets/</c>, or the docs page that shows it.
    /// </summary>
    [Fact]
    public void Every_code_fence_is_copied_from_the_file_it_names()
    {
        var fences = Fence().Matches(_text);
        var sourced = SourcedFence().Matches(_text);

        sourced.Count.ShouldBe(fences.Count, "every fence is preceded by <!-- from: <repository path> -->");
        foreach (Match fence in sourced)
        {
            var source = Path.Combine(_root, fence.Groups["path"].Value);
            File.Exists(source).ShouldBeTrue($"{fence.Groups["path"].Value} does not exist");
            var lines = File.ReadAllLines(source).Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);

            fence.Groups["code"].Value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
                .Where(line => !lines.Contains(line))
                .ShouldBeEmpty($"lines of the fence from {fence.Groups["path"].Value} that file does not have");
        }
    }

    private static bool InBaselines(string baselines, string name) =>
        Regex.IsMatch(baselines, $@"\b{name}(?:Attribute)?\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [GeneratedRegex("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex(@"<!-- from: (?<path>\S+) -->\n```\w*\n(?<code>.*?)\n```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SourcedFence();

    [GeneratedRegex("`[^`\n]+`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    /// <summary>A name shaped like Alvo's API: an interface, a builder or route verb, an <c>Alvo…</c> type, an event verb.</summary>
    [GeneratedRegex(@"\b(?:I[A-Z][A-Za-z]+|(?:Add|Map|Alvo|On|Use)[A-Z][A-Za-z]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApiName();

    [GeneratedRegex(@"https://alvo\.burgyn\.online/extending/(?<slug>[a-z0-9-]+)/", RegexOptions.CultureInvariant)]
    private static partial Regex DocsLink();
}
