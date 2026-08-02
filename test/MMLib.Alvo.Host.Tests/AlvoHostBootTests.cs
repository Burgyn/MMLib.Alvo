using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone host's own definition of done: a mounted descriptor, and nothing else, becomes a
/// working backend.
/// </summary>
public class AlvoHostBootTests
{
    /// <summary>
    /// A row round-trips through the routes the mounted descriptor declared. "The host started" is not this
    /// fact — the create and the read-back are.
    /// </summary>
    [Fact]
    public async Task A_row_round_trips_through_the_entity_the_mounted_descriptor_declares()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-1", ["city"] = "Košice" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var location = created.Headers.Location!.ToString();

        using var read = await world.GetAsync(location);

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["code"]!.GetValue<string>().ShouldBe("W-1");
    }

    /// <summary>
    /// The non-vacuity control for the fact above: the host maps the descriptor's entities and only those, so
    /// a name it does not declare has no route. Without this, a host that mapped a catch-all would pass.
    /// </summary>
    [Fact]
    public async Task An_entity_the_descriptor_does_not_declare_has_no_route()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.GetAsync("/api/pallets");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The host does not listen until the descriptor applied, so a bad descriptor is a failed start rather
    /// than a running backend with no tables. This is also what makes the container's liveness probe
    /// meaningful: answering at all proves the apply succeeded.
    /// </summary>
    /// <remarks>
    /// The failure has to <em>name the descriptor</em>, not merely be a failure: a start that threw for an
    /// unrelated reason — a mistyped configuration key, a missing driver — would satisfy "something threw"
    /// just as well, and this fact would then pass while proving nothing about the apply.
    /// </remarks>
    [Fact]
    public async Task A_descriptor_that_cannot_apply_stops_the_host_from_starting()
    {
        var missing = AlvoHostWorld.DescriptorPath("no-such-descriptor.alvo.json");

        var failure = await Should.ThrowAsync<Exception>(
            () => AlvoHostWorld.StartAsync(missing, overrides: null));

        failure.ShouldNotBeOfType<ShouldAssertException>();
        failure.Message.ShouldContain("no-such-descriptor.alvo.json");
    }

    /// <summary>
    /// §2.14's acceptance criterion — "image nikdy nedodáva prednastavené prihlásenie" — as a fact: a host
    /// with no configured credential exposes no way in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on a <b>write</b>, deliberately not on a list: this descriptor's rules are row predicates, so
    /// an anonymous list is an honest 200 with zero visible rows and says nothing about who refused —
    /// <c>DataApiAuthTests</c> makes the same choice for the same reason, and it is where the two 403s (policy
    /// versus scope gate) are told apart by problem type.
    /// </para>
    /// <para>
    /// Two refusals, because they are two claims. An anonymous caller is a context rather than a 401
    /// (deviation 23), so the descriptor's own default-deny answers 403; and the credential every other fact
    /// here uses is <em>not</em> one the host seeded for itself, so presenting it earns 401 — presented,
    /// unresolvable. <see cref="A_row_round_trips_through_the_entity_the_mounted_descriptor_declares"/> is the
    /// control that the very same create succeeds once a key is configured.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_host_with_no_configured_key_grants_nobody_anything()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: NoDevKeys());

        using var anonymous = await world.SendAnonymouslyAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-2" });
        using var presented = await world.SendAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-3" });

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        presented.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The same acceptance criterion, one layer earlier: the file that ships <em>inside the image</em> declares
    /// no credential of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fact above can only see a host configured by its caller. The realistic way a preset login reaches
    /// an operator is a dev key added to the host's own <c>appsettings.json</c> for convenience, which no
    /// runtime fact can distinguish from a key the deployment configured — so it is asserted against the file.
    /// </para>
    /// <para>
    /// <b>Every</b> <c>appsettings*.json</c>, not the one file. <c>Microsoft.NET.Sdk.Web</c>'s default
    /// <c>Content</c> glob publishes all of them into the image, and an operator running the demo image with
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> — a normal thing to do — activates
    /// <c>appsettings.Development.json</c>. Naming one file would leave a one-line way to ship a credential
    /// past a green suite. The enumeration is asserted non-empty for the same reason: a glob that matches
    /// nothing passes every claim made about its members.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_hosts_own_settings_declare_no_credential()
    {
        var hostProject = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Host");
        var settingsFiles = Directory.GetFiles(hostProject, "appsettings*.json");

        settingsFiles.ShouldNotBeEmpty(
            $"no appsettings*.json was found under {hostProject}, so this fact would assert nothing about "
            + "the files the image actually ships");

        foreach (var file in settingsFiles)
        {
            var alvo = JsonNode.Parse(File.ReadAllText(file))?[AlvoHost.ConfigurationSection];

            alvo?["Auth"].ShouldBeNull(
                $"the image must never ship a preset login (§2.14), and the dev key {Path.GetFileName(file)} "
                + "declares is one every deployment of the image would inherit — the SDK's default Content "
                + "glob publishes every appsettings*.json, not just appsettings.json");
        }
    }

    /// <summary>Liveness answers without a credential — a probe cannot present one.</summary>
    [Fact]
    public async Task Liveness_answers_an_unauthenticated_probe()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>An unknown provider name is refused by name, with the two that exist listed.</summary>
    [Fact]
    public async Task An_unknown_database_provider_is_refused_with_the_choices_named()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:Database:Provider"] = "cosmos",
        };

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => AlvoHostWorld.StartAsync(overrides: overrides));

        failure.Message.ShouldContain("cosmos");
        failure.Message.ShouldContain("sqlite");
        failure.Message.ShouldContain("postgresql");
    }

    private static Dictionary<string, string?> NoDevKeys() =>
        new(StringComparer.Ordinal)
        {
            ["Alvo:Auth:DevKeys:0:KeyId"] = null,
            ["Alvo:Auth:DevKeys:0:Secret"] = null,
            ["Alvo:Auth:DevKeys:0:User"] = null,
            ["Alvo:Auth:DevKeys:0:Roles:0"] = null,
            ["Alvo:Auth:DevKeys:0:Roles:1"] = null,
            ["Alvo:Auth:DevKeys:0:Scopes:0"] = null,
            ["Alvo:Auth:DevKeys:0:Scopes:1"] = null,
        };
}
