using MMLib.Alvo.DocsGen.Llms;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Repo;

/// <summary>
/// The developer-only <c>alvo-host-extensions</c> skill against what it names: the host packages' public API, the docs pages
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

    /// <summary>The public API of the packages an embedded host's code compiles against.</summary>
    private static readonly string[] _hostBaselines =
    [
        "test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt",
        "test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt",
        "test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt",
        "test/MMLib.Alvo.TestHost.Tests/PublicApi.MMLib.Alvo.TestHost.verified.txt",
    ];

    /// <summary>Names a code span uses for the reader's own code, not Alvo's: the property a typed <c>Set</c> picks.</summary>
    private static readonly string[] _illustrative = ["Field"];

    /// <summary>.NET's own types a span names beside Alvo's: the fake clock the TestHost takes.</summary>
    private static readonly string[] _dotnet = ["FakeTimeProvider"];

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

    /// <summary>
    /// Every PascalCase name inside a code span — a type, a member, an enum value — is a symbol of the packages a host
    /// references, so a rename or a removal in their public API fails here instead of in a reader's build.
    /// </summary>
    /// <remarks>
    /// Spans are paired over the text with its line breaks folded, so a span the Markdown wraps is still read whole.
    /// Only the four baselines a host's code compiles against count: a name that survives only in, say, the dashboard's
    /// Blazor API (<c>EventCallback OnSaved</c>) is not the builder method the skill means.
    /// </remarks>
    [Fact]
    public void Every_api_name_the_skill_spells_in_code_is_in_a_host_package_baseline()
    {
        var baselines = string.Join('\n', _hostBaselines.Select(path => File.ReadAllText(Path.Combine(_root, path))));
        var names = NamesInCode(_text);

        names.ShouldContain("AsSystem", "the scan must reach member names, not only type names");
        names.ShouldContain("AlvoWriteRejectedException", "the scan must read a span the Markdown wraps across lines");
        names.Count.ShouldBeGreaterThan(50, "the name scan matched almost nothing; the skill or the pattern moved");
        names.Where(name => !InBaselines(baselines, name))
            .ShouldBeEmpty("named in the skill's code, absent from the Abstractions, MMLib.Alvo, Testing and TestHost baselines");
    }

    /// <summary>The PascalCase names inside the code spans of <paramref name="text"/>, fences left out, each once.</summary>
    internal static IReadOnlyList<string> NamesInCode(string text) =>
    [
        .. CodeSpan().Matches(LineBreak().Replace(Fence().Replace(text, string.Empty), " "))
            .SelectMany(span => PascalName().Matches(span.Value).Select(match => match.Value))
            .Where(name => !GenericParameter().IsMatch(name) && !_illustrative.Contains(name) && !_dotnet.Contains(name))
            .Distinct(StringComparer.Ordinal),
    ];

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

    [GeneratedRegex("`[^`]+`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"[ \t]*\n[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    /// <summary>A PascalCase identifier with at least one lower-case letter, so <c>ALVO1001</c> and <c>UTC</c> are not names.</summary>
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]*[a-z][A-Za-z0-9]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex PascalName();

    /// <summary>A generic type parameter as the skill writes them: <c>TEvent</c>, <c>TInput</c>, <c>TJob</c>.</summary>
    [GeneratedRegex("^T(?:[A-Z][a-z]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex GenericParameter();

    [GeneratedRegex(@"https://alvo\.burgyn\.online/extending/(?<slug>[a-z0-9-]+)/?", RegexOptions.CultureInvariant)]
    private static partial Regex DocsLink();
}
