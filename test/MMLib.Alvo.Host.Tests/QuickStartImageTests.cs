using MMLib.Alvo.Descriptor;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The no-clone quick start (<c>docker-compose.quickstart.yml</c>) and the image it runs agree with each other
/// and with the repository's own stack — checked as text, without Docker, so it is a ring0 fact.
/// </summary>
/// <remarks>
/// The behaviour itself — the stack comes up ready, the demo key authenticates, the dashboard signs in — is
/// <c>scripts/test-quickstart</c>, which <c>.github/workflows/image.yml</c> runs on every image it builds. These
/// facts cover what that run cannot see from inside one image: a Dockerfile that stops shipping an example the
/// quick start points at only fails there after a full image build, and a renamed secret variable fails nowhere
/// at all, because each file is internally consistent.
/// </remarks>
public sealed partial class QuickStartImageTests
{
    private static string Root => RepositoryRoot.Find();

    private static string QuickStart => File.ReadAllText(Path.Combine(Root, "docker-compose.quickstart.yml")).ReplaceLineEndings("\n");

    private static string LocalStack => File.ReadAllText(Path.Combine(Root, "docker-compose.yml")).ReplaceLineEndings("\n");

    private static string Dockerfile =>
        File.ReadAllText(Path.Combine(Root, "src", "MMLib.Alvo.Host", "Dockerfile")).ReplaceLineEndings("\n");

    /// <summary>The quick start runs the published image unless told otherwise.</summary>
    [Fact]
    public void The_quick_start_runs_the_published_image_by_default()
    {
        QuickStart.ShouldContain("image: ${ALVO_IMAGE:-ghcr.io/burgyn/alvo:edge}");
    }

    /// <summary>
    /// Same credential contract as <c>docker-compose.yml</c>: the demo key's secret is the same variable, and
    /// demanded with <c>:?</c> rather than defaulted (§2.14 — the image ships no credential).
    /// </summary>
    [Fact]
    public void The_quick_start_demands_the_same_secret_as_the_local_stack()
    {
        var local = SecretVariable().Match(LocalStack);
        local.Success.ShouldBeTrue("docker-compose.yml no longer demands the demo key's secret with ${VAR:?…}");

        var quick = SecretVariable().Match(QuickStart);
        quick.Success.ShouldBeTrue("docker-compose.quickstart.yml must demand the demo key's secret with ${VAR:?…}");
        quick.Groups["name"].Value.ShouldBe(local.Groups["name"].Value);
    }

    /// <summary>
    /// Loopback only, like the local stack: the demo key is admin + read + write. Every published port, not one
    /// string somewhere in the file — a second <c>"8080:8080"</c> entry beside the loopback one would publish the
    /// same key on every interface.
    /// </summary>
    [Fact]
    public void The_quick_start_publishes_on_loopback_only()
    {
        var mappings = PortsBlock().Matches(QuickStart)
            .SelectMany(block => PortItem().Matches(block.Groups["items"].Value))
            .Select(item => item.Groups["mapping"].Value)
            .ToList();

        mappings.ShouldBe(["127.0.0.1:8080:8080"], "the quick start publishes exactly one port, on loopback");
    }

    /// <summary>
    /// The demo key authenticates against every example the image ships. A key naming one role the running
    /// descriptor does not declare is refused as a whole with a bare 401 (#131), so each of its roles has to be
    /// one the role catalogue of <em>every</em> shipped descriptor resolves — which today means the built-ins.
    /// </summary>
    [Fact]
    public void The_demo_keys_roles_resolve_against_every_shipped_example()
    {
        var roles = DemoKeyRole().Matches(QuickStart).Select(match => match.Groups["role"].Value).ToList();
        roles.ShouldNotBeEmpty("no Alvo__Auth__DevKeys__0__Roles__N line was found, so this fact would check nothing");

        foreach (var example in ShippedExample().Matches(Dockerfile).Select(match => match.Groups["path"].Value))
        {
            var descriptor = AlvoDescriptor.Parse(File.ReadAllText(Path.Combine(Root, "examples", example)));
            var catalog = RoleCatalog.FromDescriptor(descriptor);

            roles.Where(role => !catalog.TryGet(role, out _)).ShouldBeEmpty(
                $"the demo key would be refused whole (#131) by the shipped example {example}");
        }
    }

    /// <summary>
    /// The bootstrap administrator's password reaches the host as a file: the path the host is told is the mount
    /// of the secret the service declares, and that secret is sourced from <c>ALVO_ADMIN_PASSWORD</c>.
    /// </summary>
    [Fact]
    public void The_admin_password_is_wired_as_a_secret_file()
    {
        var path = BootstrapPasswordFile().Match(QuickStart);
        path.Success.ShouldBeTrue("Alvo__Admin__BootstrapPasswordFile must point under /run/secrets/");
        var name = path.Groups["name"].Value;

        QuickStart.ShouldContain($"    secrets:\n      - {name}\n", Case.Sensitive, "the alvo service must mount that secret");
        QuickStart.ShouldContain($"\nsecrets:\n  {name}:\n    environment: ALVO_ADMIN_PASSWORD\n", Case.Sensitive,
            "the secret must be sourced from ALVO_ADMIN_PASSWORD, never a literal or a file in the repository");
        QuickStart.ShouldNotContain("Alvo__Admin__BootstrapPassword:", Case.Sensitive, "the host refuses a password given as a value");
    }

    /// <summary>
    /// The image ships exactly the examples a host can apply: every runnable one, and neither
    /// <c>_negative/</c> nor an example marked not runnable.
    /// </summary>
    [Fact]
    public void The_image_ships_exactly_the_runnable_examples()
    {
        var shipped = ShippedExample().Matches(Dockerfile)
            .Select(match => match.Groups["path"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        var runnable = AlvoExamples.Runnable()
            .Select(path => Path.GetRelativePath(Path.Combine(Root, "examples"), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        runnable.ShouldNotBeEmpty("no runnable example was found, so this fact would compare nothing");
        shipped.ShouldBe(runnable, "the Dockerfile's COPY examples/… lines must list every runnable example and nothing else");
    }

    /// <summary>The descriptor the quick start points at by default is one the image ships.</summary>
    [Fact]
    public void The_default_descriptor_is_one_the_image_ships()
    {
        var descriptor = DefaultDescriptor().Match(QuickStart);
        descriptor.Success.ShouldBeTrue("docker-compose.quickstart.yml must default Alvo__DescriptorPath via ${ALVO_DESCRIPTOR:-…}");

        var shipped = ShippedExample().Matches(Dockerfile).Select(match => $"/alvo/examples/{match.Groups["path"].Value}");
        shipped.ShouldContain(descriptor.Groups["path"].Value);
    }

    [GeneratedRegex(@"Alvo__Auth__DevKeys__0__Secret: \$\{(?<name>[A-Z_]+):\?")]
    private static partial Regex SecretVariable();

    [GeneratedRegex(@"^COPY examples/(?<path>(?<dir>[^/ ]+)/[^/ ]+\.alvo\.json) /alvo/examples/\k<dir>/$", RegexOptions.Multiline)]
    private static partial Regex ShippedExample();

    [GeneratedRegex(@"Alvo__DescriptorPath: \$\{ALVO_DESCRIPTOR:-(?<path>[^}]+)\}")]
    private static partial Regex DefaultDescriptor();

    [GeneratedRegex(@"^ +ports:\n(?<items>(?: +- .+\n)+)", RegexOptions.Multiline)]
    private static partial Regex PortsBlock();

    [GeneratedRegex(@"- ""?(?<mapping>[^""\n]+)""?\n")]
    private static partial Regex PortItem();

    [GeneratedRegex(@"^ +Alvo__Auth__DevKeys__0__Roles__\d+: (?<role>\S+)$", RegexOptions.Multiline)]
    private static partial Regex DemoKeyRole();

    [GeneratedRegex(@"^ +Alvo__Admin__BootstrapPasswordFile: /run/secrets/(?<name>\w+)$", RegexOptions.Multiline)]
    private static partial Regex BootstrapPasswordFile();
}
