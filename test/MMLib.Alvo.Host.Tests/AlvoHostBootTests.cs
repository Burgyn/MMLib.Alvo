using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Identity;
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
        var body = await read.ReadJsonObjectAsync();
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
    /// <para>
    /// <b>Read through the configuration binder, not through <c>JsonNode</c>.</b> A <c>JsonNode</c> indexer is
    /// ordinal case-sensitive and <c>Microsoft.Extensions.Configuration.Json</c> is not, so a lowercase
    /// <c>"alvo"</c> section — or <c>"Alvo"</c> with a lowercase <c>"auth"</c> child, or a single flattened
    /// <c>"Alvo:Auth"</c> key — binds a working admin credential into the published image while a hand-rolled
    /// key lookup reports nothing. That is the same hole one layer down from the one this fact already fixed
    /// once: the file <em>enumeration</em> was widened from one name to the glob and the <em>key lookup</em>
    /// was not. Asking the very parser the host binds with removes the class rather than the instance — any
    /// spelling the binder accepts is a spelling this sees.
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
            var settings = new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();

            settings.GetSection($"{AlvoHost.ConfigurationSection}:Auth").Exists().ShouldBeFalse(
                $"the image must never ship a preset login (§2.14), and the dev key {Path.GetFileName(file)} "
                + "declares is one every deployment of the image would inherit — the SDK's default Content "
                + "glob publishes every appsettings*.json, not just appsettings.json");
        }
    }

    /// <summary>
    /// One start loads the descriptor once. The host used to load it twice: once to apply it itself, and once
    /// more inside the boot that now owns the apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count is the fact. Both passes produced the same schema and neither logged anything the other did
    /// not, so a duplicated stage 0 — parse, JSON-Schema validate, map, compile every rule — was invisible in a
    /// green suite and in a container's log alike. It is also the only observable difference between "the host
    /// stopped applying" and "the host still applies and the boot happens to find nothing to do", which is
    /// exactly the shape a half-finished collapse would leave behind.
    /// </para>
    /// <para>
    /// A working request is asserted beside it so the single read cannot be a host that read the descriptor and
    /// then served nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_start_loads_the_descriptor_once()
    {
        DescriptorReadCounter? reads = null;

        await using var world = await AlvoHostWorld.StartAsync(
            configure: builder => reads = DescriptorReadCounter.RegisteredOn(builder));

        using var listed = await world.GetAsync("/api/warehouses");

        listed.StatusCode.ShouldBe(
            HttpStatusCode.OK, "a host that read the descriptor once must still serve what it declared");
        reads.ShouldNotBeNull("the fixture must really have wrapped the descriptor source");
        reads.Reads.ShouldBe(
            1,
            "the boot owns the apply, so one start is one load-validate-map-compile pass — two means the host "
            + "is applying the descriptor again beside it");
    }

    /// <summary>Liveness answers without a credential — a probe cannot present one.</summary>
    [Fact]
    public async Task Liveness_answers_an_unauthenticated_probe()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A credential the startup validation refuses fails the start <b>with the database untouched</b> — not
    /// after the descriptor's DDL has already been committed against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema assertion is the fact, not the throw. <c>ValidateOnStart</c> runs from
    /// <c>app.StartAsync()</c>, which is after <see cref="AlvoHost.BuildAsync"/> has applied the descriptor,
    /// so a host that only validated there <em>also</em> refused this start — with the migration committed.
    /// And that is not merely untidy: the previous descriptor is destructive relative to the schema the failed
    /// start wrote, so <c>EnsureApplied</c> refuses the rollback too and the deployment cannot boot at all.
    /// One typo in an environment variable, one unbootable database.
    /// </para>
    /// <para>
    /// An unparseable scope rather than a missing key, because "no credential at all" is a valid host
    /// (<see cref="A_host_with_no_configured_key_grants_nobody_anything"/>) and would prove nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_credential_the_startup_validation_refuses_leaves_the_database_untouched()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            var failure = await Should.ThrowAsync<OptionsValidationException>(
                () => AlvoHostWorld.StartAsync(overrides: MistypedScope(), databasePath: databasePath));

            failure.Message.ShouldContain("*:reed");
            AlvoHostWorld.TableNamesIn(databasePath).ShouldBeEmpty(
                "the descriptor's DDL must not be committed against a database the host is about to refuse "
                + "to boot over — rolling the deployment back does not undo it");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// The same acceptance criterion, one subsystem over: the image ships no <b>bootstrap</b>
    /// credential either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read through <c>ConfigurationBuilder.AddJsonFile</c>, not through <c>JsonNode</c>, and that is
    /// the trap this repository already recorded once: a <c>JsonNode</c> indexer is ordinal
    /// case-sensitive and <c>Microsoft.Extensions.Configuration.Json</c> is not, so a lowercase
    /// <c>"alvo"</c> section — or <c>"Alvo"</c> with a lowercase <c>"admin"</c> child, or a single
    /// flattened <c>"Alvo:Admin:BootstrapPasswordFile"</c> key — binds a working administrator into the
    /// published image while a hand-rolled lookup reports nothing. Asking the very parser the host binds
    /// with removes the class rather than the instance.
    /// </para>
    /// <para>
    /// <b>Every</b> <c>appsettings*.json</c>, for the reason the neighbouring fact gives: the SDK's
    /// default Content glob publishes all of them, and an operator running the image with
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> activates <c>appsettings.Development.json</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_hosts_own_settings_declare_no_bootstrap_credential()
    {
        var hostProject = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Host");
        var settingsFiles = Directory.GetFiles(hostProject, "appsettings*.json");

        settingsFiles.ShouldNotBeEmpty(
            $"no appsettings*.json was found under {hostProject}, so this fact would assert nothing "
            + "about the files the image actually ships");

        foreach (var file in settingsFiles)
        {
            var settings = new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();

            settings.GetSection(AlvoIdentity.ConfigurationSection).Exists().ShouldBeFalse(
                "the image must never ship a preset administrator (§2.14), and the bootstrap "
                + $"{Path.GetFileName(file)} declares is one every deployment of the image would "
                + "inherit — the SDK's default Content glob publishes every appsettings*.json");
        }
    }

    /// <summary>
    /// <b><c>Alvo__Admin__BootstrapEmail</c> reaches a running container.</b> The registration, the section
    /// binding and the identity store's database are one claim here, because they are one mechanism: the
    /// administrator only exists if all three are right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written against the started host, never against a hand-assembled <c>ServiceCollection</c>.</b> A
    /// fact that builds its own collection and calls <c>AddAlvoIdentity</c> itself measures the extension
    /// method — which has its own suite — and would stay green with the three lines in
    /// <see cref="AlvoHost.CreateBuilder"/> deleted. That is the shape #142 recorded: a check that passes
    /// against an empty implementation.
    /// </para>
    /// <para>
    /// <b>The seeded identifier is read off the database file, not off the container.</b> Asking the host for
    /// the administrator it published and then asking the same host whether that is the administrator is a
    /// tautology; reading it out of the very SQLite file <c>Alvo__Database__SqliteConnectionString</c> names
    /// is the only thing that can say <c>AlvoDatabaseSelector.IdentityStore</c> pointed the identity store at
    /// the database the rest of Alvo uses rather than at one of its own.
    /// </para>
    /// <para>
    /// And the answer is asserted <em>true</em>, which the core's default cannot produce: <c>NoBootstrapAdmin</c>
    /// answers <see langword="false"/> for everyone, so a host that never registered the subsystem, or bound the
    /// wrong section onto it, fails here rather than passing quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_configured_bootstrap_administrator_reaches_the_running_container()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        var secret = WriteSecret();

        try
        {
            await using var world = await AlvoHostWorld.StartAsync(
                overrides: Bootstrap(secret), databasePath: databasePath);

            var seeded = SeededAdministratorIn(databasePath);

            seeded.ShouldNotBeNull(
                $"no row for {BootstrapEmail} exists in the host's own database, so either the identity "
                + "subsystem was never registered or its store was pointed at a different database");
            world.Services.GetRequiredService<IAlvoBootstrapAdmin>()
                .IsBootstrapAdmin(new UserId(seeded.Value)).ShouldBeTrue(
                    "the container's IAlvoBootstrapAdmin must be the identity package's, holding the "
                    + "administrator Alvo:Admin:BootstrapEmail named — the core's default answers false "
                    + "for everyone");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
            File.Delete(secret);
        }
    }

    /// <summary>The address the bootstrap administrator is seeded under.</summary>
    private const string BootstrapEmail = "bootstrap-admin@example.test";

    /// <summary>The bootstrap the host would read out of a container's environment and a mounted secret.</summary>
    /// <param name="secret">The mounted password file.</param>
    private static Dictionary<string, string?> Bootstrap(string secret) =>
        new(StringComparer.Ordinal)
        {
            ["Alvo:Admin:BootstrapEmail"] = BootstrapEmail,
            ["Alvo:Admin:BootstrapPasswordFile"] = secret,
        };

    /// <summary>Writes a password strong enough for Identity's default policy into a temporary file.</summary>
    private static string WriteSecret()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-bootstrap-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Str0ng!Passw0rd");

        return path;
    }

    /// <summary>
    /// The identifier of the administrator seeded into <paramref name="databasePath"/>, or
    /// <see langword="null"/> when nothing seeded one there.
    /// </summary>
    /// <remarks>
    /// The table name is spelled here rather than read from <c>AlvoFrameworkTables</c> on purpose: it is a
    /// reserved wire-level name, and a fact that took it from the same constant the mapping does would go on
    /// passing after a rename that stranded every existing operator account.
    /// </remarks>
    /// <param name="databasePath">The database the host was configured with.</param>
    private static Guid? SeededAdministratorIn(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder($"Data Source={databasePath}") { Pooling = false }.ToString());
        connection.Open();

        return AdministratorId(connection);
    }

    /// <summary>Reads the one seeded administrator's identifier, tolerating a database with no such table.</summary>
    /// <param name="connection">An open connection to the host's database.</param>
    private static Guid? AdministratorId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM alvo_identity_users WHERE Email = $email;";
        command.Parameters.AddWithValue("$email", BootstrapEmail);

        try
        {
            return command.ExecuteScalar() is string id ? Guid.Parse(id) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>One dev-key scope with a typo in it — the shape a hand-written environment variable takes.</summary>
    private static Dictionary<string, string?> MistypedScope() =>
        new(StringComparer.Ordinal) { ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:reed" };

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
