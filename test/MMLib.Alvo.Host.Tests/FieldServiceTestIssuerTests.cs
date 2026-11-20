using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The field-service stack's test OpenID issuer (S6 D3/D4) holds together, and its trust stays inside the demo —
/// checked as text, without Docker, so it is a ring0 fact.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour itself — a token signed by the issuer's key signs a person in, one signed by any other key does not,
/// and the sink serves the key's public half only — is <c>test/teapie-field-service/170-SelfService/001-sign-in</c>,
/// against the real container. These facts cover what that run cannot see: three files that must agree (the compose
/// authority, the committed discovery document, the path the suite registers the key set at), each of which stays
/// internally consistent when one of them drifts; and the two properties that must hold for every file in the
/// repository rather than for the one run — no private key is committed anywhere, and nothing but the
/// Development-only demo stack trusts <c>http</c> metadata.
/// </para>
/// <para>
/// <b>Deviation from the S6 plan's D4</b>, recorded here because these facts are its guard: the plan committed one
/// private key; the suite instead makes one on the machine that runs it and registers the public half in the sink per
/// run (<c>_shared/TestIssuer.csx</c> says why it is kept between runs). So these facts hold that no key is committed
/// at all, rather than that a committed one stays under <c>test/</c>.
/// </para>
/// </remarks>
public sealed partial class FieldServiceTestIssuerTests
{
    private const string GoogleIssuer = "https://accounts.google.com";

    private static string Root => RepositoryRoot.Find();

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path])).ReplaceLineEndings("\n");

    private static string Stack => Read("docker-compose.field-service.yml");

    private static string TestIssuerScript => Read("test", "teapie-field-service", "_shared", "TestIssuer.csx");

    private static JsonElement Discovery()
    {
        using var mapping = JsonDocument.Parse(Read("examples", "field-service", "sink", "mappings", "test-oidc-issuer.json"));
        return mapping.RootElement.GetProperty("mappings").EnumerateArray().Single().Clone();
    }

    /// <summary>
    /// The sink's discovery document names Google's issuer — the preset finds its provider by it — and the key set the
    /// sink serves, signed with the preset's only algorithm; and it is served where the stack's authority looks.
    /// </summary>
    [Fact]
    public void The_discovery_document_names_googles_issuer_and_the_sinks_key_set()
    {
        var discovery = Discovery();
        var document = discovery.GetProperty("response").GetProperty("jsonBody");

        document.GetProperty("issuer").GetString().ShouldBe(GoogleIssuer);
        document.GetProperty("jwks_uri").GetString().ShouldBe("http://localhost:9090/oidc/jwks.json");
        document.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(alg => alg.GetString())
            .ShouldBe(["RS256"]);

        var authority = new Uri(Authority().Match(Stack).Groups["url"].Value);
        discovery.GetProperty("request").GetProperty("urlPath").GetString()
            .ShouldBe(authority.AbsolutePath + "/.well-known/openid-configuration");
        authority.Authority.ShouldBe(new Uri(document.GetProperty("jwks_uri").GetString()!).Authority);
    }

    /// <summary>The suite registers the key set at exactly the path the discovery document's <c>jwks_uri</c> names.</summary>
    [Fact]
    public void The_suite_registers_its_key_set_where_the_discovery_document_points()
    {
        var jwksUri = new Uri(Discovery().GetProperty("response").GetProperty("jsonBody").GetProperty("jwks_uri").GetString()!);

        var registered = JwksPath().Match(TestIssuerScript);
        registered.Success.ShouldBeTrue("TestIssuer.csx no longer declares TestIssuerJwksPath, so this fact would check nothing");
        registered.Groups["path"].Value.ShouldBe(jwksUri.AbsolutePath);
    }

    /// <summary>
    /// The committed mapping carries no key material at all — not even a public one. The key set is the run's, and a
    /// committed <c>keys</c> member would be served beside it, or instead of it.
    /// </summary>
    [Fact]
    public void The_committed_issuer_carries_no_key_material()
    {
        string[] keyMembers = ["keys", "kty", "n", "e", "d", "p", "q", "dp", "dq", "qi", "x5c"];
        var directory = Path.Combine(Root, "examples", "field-service", "sink", "mappings");

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var mapping = JsonDocument.Parse(File.ReadAllText(file));
            MemberNames(mapping.RootElement).Intersect(keyMembers).ShouldBeEmpty($"{Path.GetFileName(file)} carries key material");
        }
    }

    /// <summary>
    /// No PEM private key block exists in any file the repository tracks — the suite makes its key where it runs, so a
    /// committed one is a mistake wherever it is. Tracked files only (<c>git ls-files</c>, which includes what is
    /// staged): a contributor's own untracked key file is not the repository's.
    /// </summary>
    [Fact]
    public void No_private_key_is_committed()
    {
        var offenders = TrackedFiles()
            .Select(file => Path.Combine(Root, file))
            .Where(File.Exists)
            .Where(file => PemPrivateKey().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(Root, file))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// The load stack composes the demo's file, so every demo-only setting the demo adds for sign-in — the environment,
    /// the issuer, the secrets key, the rate budgets — is reset in <c>test/load/docker-compose.load.yml</c>, along with
    /// the secret's mount: the load baselines describe a host without them.
    /// </summary>
    [Fact]
    public void The_load_stack_resets_the_demos_sign_in()
    {
        var demoOnly = DemoOnlySetting().Matches(Stack).Select(match => match.Groups["name"].Value).ToList();
        demoOnly.ShouldNotBeEmpty("the demo stack no longer configures sign-in, so this fact would check nothing");

        var load = Read("test", "load", "docker-compose.load.yml");
        demoOnly.Where(name => !load.Contains($"\n      {name}: !reset null\n", StringComparison.Ordinal))
            .ShouldBeEmpty("the load stack would measure a host its baselines do not describe");
        load.ShouldContain("\n    secrets: !reset []\n");
    }

    /// <summary>
    /// <c>AllowHttpMetadata</c> is switched on in exactly one place — the demo stack's <c>alvo</c> service, beside
    /// <c>ASPNETCORE_ENVIRONMENT: Development</c>, the only environment the product accepts it in — and in no other
    /// compose file.
    /// </summary>
    [Fact]
    public void The_demo_stack_trusts_http_metadata_only_in_development()
    {
        var services = Service().Matches(Stack).Where(service => service.Value.Contains("AllowHttpMetadata", StringComparison.Ordinal)).ToList();
        services.Select(service => service.Groups["name"].Value).ShouldBe(["alvo"]);
        AllowHttpMetadata().Count(services[0].Value).ShouldBe(1);
        services[0].Value.ShouldContain("\n      ASPNETCORE_ENVIRONMENT: Development\n");

        var others = Directory.EnumerateFiles(Root, "docker-compose*.yml")
            .Where(file => Path.GetFileName(file) != "docker-compose.field-service.yml")
            .Where(file => File.ReadAllText(file).Contains("AllowHttpMetadata", StringComparison.Ordinal))
            .Select(Path.GetFileName);
        others.ShouldBeEmpty();
    }

    /// <summary>
    /// The audience the stack accepts and the one the suite mints for are one value, from one file: compose demands it
    /// with <c>:?</c>, <c>scripts/test-e2e</c> hands the same variable to TeaPie, and the example environment agrees.
    /// </summary>
    [Fact]
    public void The_stack_and_the_suite_read_one_client_id()
    {
        var clientId = ClientIdDefinition().Match(Read("examples", "field-service", "demo-identities.env"));
        clientId.Success.ShouldBeTrue("demo-identities.env no longer defines ALVO_FS_GOOGLE_CLIENT_ID");

        Stack.ShouldContain("Alvo__Accounts__Providers__Google__ClientIds__0: ${ALVO_FS_GOOGLE_CLIENT_ID:?");
        Read("scripts", "test-e2e").ShouldContain("\"fsGoogleClientId\": \"$ALVO_FS_GOOGLE_CLIENT_ID\"");

        using var example = JsonDocument.Parse(Read("test", "teapie-field-service", "env.example.json"));
        example.RootElement.GetProperty("compose").GetProperty("fsGoogleClientId").GetString()
            .ShouldBe(clientId.Groups["value"].Value);
    }

    /// <summary>
    /// The key that seals what sign-in stores is a Compose secret from the run's environment: the file path names the
    /// secret, the secret reads <c>ALVO_FS_SECRETS_KEY</c>, <c>scripts/test-e2e</c> makes it fresh, and the committed
    /// constants file never defines it.
    /// </summary>
    [Fact]
    public void The_secrets_key_comes_from_the_run_and_is_never_committed()
    {
        Stack.ShouldContain("Alvo__Secrets__EncryptionKeyFile: /run/secrets/alvo_fs_secrets_key\n");
        Stack.ShouldContain("\nsecrets:\n  alvo_fs_secrets_key:\n    environment: ALVO_FS_SECRETS_KEY\n");
        Read("scripts", "test-e2e").ShouldContain("export ALVO_FS_SECRETS_KEY=\"${ALVO_FS_SECRETS_KEY:-$(openssl rand -base64 32)}\"");
        Read("examples", "field-service", "demo-identities.env").ShouldNotContain("ALVO_FS_SECRETS_KEY");
    }

    private static IEnumerable<string> MemberNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(member => MemberNames(member.Value).Prepend(member.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(MemberNames),
        _ => [],
    };

    private static string[] TrackedFiles()
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        git.ExitCode.ShouldBe(0, "git ls-files failed, so this fact would check nothing");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex(@"^ +Alvo__Accounts__Providers__Google__Authority: (?<url>\S+)$", RegexOptions.Multiline)]
    private static partial Regex Authority();

    [GeneratedRegex(@"^const string TestIssuerJwksPath = ""(?<path>[^""]+)"";$", RegexOptions.Multiline)]
    private static partial Regex JwksPath();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----")]
    private static partial Regex PemPrivateKey();

    [GeneratedRegex(@"^  (?<name>[a-z][a-z0-9-]*):\n(?:(?: {4}.*)?\n)+", RegexOptions.Multiline)]
    private static partial Regex Service();

    [GeneratedRegex(@"^      (?<name>ASPNETCORE_ENVIRONMENT|Alvo__Accounts__\S+|Alvo__Secrets__\S+|Alvo__Api__RateLimits__\S+):", RegexOptions.Multiline)]
    private static partial Regex DemoOnlySetting();

    [GeneratedRegex(@"^ +Alvo__Accounts__Providers__\w+__AllowHttpMetadata: ""true""$", RegexOptions.Multiline)]
    private static partial Regex AllowHttpMetadata();

    [GeneratedRegex(@"^ALVO_FS_GOOGLE_CLIENT_ID=(?<value>\S+)$", RegexOptions.Multiline)]
    private static partial Regex ClientIdDefinition();
}
