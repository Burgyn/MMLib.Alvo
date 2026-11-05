namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The load harness's hooks image (<c>tools/MMLib.Alvo.Load.Host</c>) is the standalone image with stated differences and
/// nothing else — checked as text, without Docker, so a drift fails ring0 instead of the calibration run on a release tag,
/// the first place that image is ever built.
/// </summary>
/// <remarks>
/// The hook shapes are published as a difference from the standalone image's <c>create</c>, so a build difference the two
/// files do not state — another base image, another publish flag — would be measured as a hook's cost.
/// </remarks>
public sealed class LoadHostImageParityTests
{
    private const string StandaloneProject = "src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj";
    private const string LoadProject = "tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj";

    private static string Root => RepositoryRoot.Find();

    /// <summary>
    /// Instruction for instruction the same, once the stated differences are taken out: the standalone image's OCI labels,
    /// its final-stage build arguments and its shipped examples; the load image's copy of its own project; and the project
    /// and assembly names that restore, publish and the entrypoint use.
    /// </summary>
    [Fact]
    public void The_load_host_dockerfile_is_the_standalone_one_modulo_its_stated_differences()
    {
        var standalone = WithoutStandaloneOnly(Instructions(Read("src", "MMLib.Alvo.Host", "Dockerfile")))
            .ConvertAll(instruction => instruction
                .Replace(StandaloneProject, LoadProject, StringComparison.Ordinal)
                .Replace("\"MMLib.Alvo.Host.dll\"", "\"MMLib.Alvo.Load.Host.dll\"", StringComparison.Ordinal));
        var load = Instructions(Read("tools", "MMLib.Alvo.Load.Host", "Dockerfile"))
            .FindAll(instruction => instruction != "COPY tools/MMLib.Alvo.Load.Host/ tools/MMLib.Alvo.Load.Host/");

        load.ShouldBe(standalone, "a build difference the hooks image does not state would be published as a hook's cost");
    }

    /// <summary>The build context is the same root, so the ignore files differ only in the line that admits the load host.</summary>
    [Fact]
    public void The_load_host_dockerignore_is_the_root_one_modulo_admitting_its_own_project()
    {
        var root = Lines(Read(".dockerignore"));
        var load = Lines(Read("tools", "MMLib.Alvo.Load.Host", "Dockerfile.dockerignore"));

        var expected = root.SelectMany(line => line == "tools/" ? ["tools/*", "!tools/MMLib.Alvo.Load.Host"] : new[] { line }).ToList();

        load.ShouldBe(expected, "the load host's ignore file must track the root one, admitting only tools/MMLib.Alvo.Load.Host");
    }

    /// <summary>The standalone image's OCI labels, its final-stage build arguments (they feed only the labels) and its examples.</summary>
    private static List<string> WithoutStandaloneOnly(List<string> instructions)
    {
        var final = instructions.FindIndex(instruction => instruction.StartsWith("FROM ", StringComparison.Ordinal) && instruction.EndsWith(" AS final", StringComparison.Ordinal));
        final.ShouldBeGreaterThan(0, "the standalone Dockerfile has no 'AS final' stage, so this fact no longer knows its shape");

        return [.. instructions.Where((instruction, index) =>
            !instruction.StartsWith("LABEL ", StringComparison.Ordinal)
            && !instruction.StartsWith("COPY examples/", StringComparison.Ordinal)
            && !(index > final && instruction.StartsWith("ARG ", StringComparison.Ordinal)))];
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([Root, .. path])).ReplaceLineEndings("\n");

    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0 && !line.StartsWith('#'))];

    /// <summary>Every Dockerfile instruction on one line: comments and blanks dropped, continuations joined.</summary>
    private static List<string> Instructions(string dockerfile)
    {
        var instructions = new List<string>();
        var pending = string.Empty;
        foreach (var line in Lines(dockerfile).Select(line => line.Trim()))
        {
            pending = pending.Length == 0 ? line : $"{pending} {line}";
            if (!pending.EndsWith('\\'))
            {
                instructions.Add(pending);
                pending = string.Empty;
            }
        }

        return instructions;
    }
}
