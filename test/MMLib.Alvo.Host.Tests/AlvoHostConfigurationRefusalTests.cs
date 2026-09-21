using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Host.Internal;
using MMLib.Alvo.Identity;
using MMLib.Alvo.Migrations;
using System.Runtime.Versioning;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// #132: a misconfigured container reads a sentence naming the thing and the fix, and the process exits with a
/// code it chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is right and must not change is that it refuses at all.</b> The issue says so in as many words, and
/// nothing here asks for a fallback, a default descriptor or a start-anyway mode. Every fact below is about the
/// <em>presentation</em> of a refusal that already happened — and about the one property that makes a refusal
/// recoverable rather than terminal: that it happens before any DDL.
/// </para>
/// <para>
/// The refusals are asserted on their <em>text</em>, not merely on a throw. A start that failed for an
/// unrelated reason — a descriptor the fixture mistyped, a missing driver — satisfies "it threw" just as well,
/// and each fact would then pass while proving nothing about the refusal it names.
/// </para>
/// </remarks>
public class AlvoHostConfigurationRefusalTests
{
    private const string MissingDescriptorPath = "/nope/missing.json";

    /// <summary>
    /// #132's own reproduction: a mount point with nothing at it names the path, the docker mount, and the
    /// environment variable.
    /// </summary>
    [Fact]
    public async Task A_missing_descriptor_is_refused_by_name_with_the_mount_fix()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(MissingDescriptorPath));

        refusal.Message.ShouldContain(MissingDescriptorPath);
        refusal.Message.ShouldContain("Alvo__DescriptorPath");
        refusal.Message.ShouldContain("-v");
    }

    /// <summary>An unknown driver name is refused with the two that exist, spelled as the operator sets them.</summary>
    /// <remarks>
    /// Raised while the container is still being built, because that is when the driver is registered — see
    /// <c>AlvoDatabaseSelector</c>. The type is the same one <c>ValidateOnStart</c> raises a moment later, so
    /// which of the two moments refused is invisible to the caller and to the operator.
    /// </remarks>
    [Fact]
    public async Task An_unknown_database_provider_is_refused_with_the_choices_named()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(overrides: Provider("mongo")));

        refusal.Message.ShouldContain("mongo");
        refusal.Message.ShouldContain(AlvoHostDatabaseOptions.Sqlite);
        refusal.Message.ShouldContain(AlvoHostDatabaseOptions.PostgreSql);
        refusal.Message.ShouldContain("Alvo__Database__Provider");
    }

    /// <summary>
    /// PostgreSQL with nowhere to connect is refused, and never silently defaulted to the SQLite file.
    /// </summary>
    /// <remarks>
    /// The driver refuses this too, but only when the boot first resolves a store — after the framework's own
    /// tables have been touched, and naming the configuration path rather than the environment spelling. The
    /// fact asserts <c>ConnectionStrings__Alvo</c> precisely because that is the half the driver's own message
    /// cannot give.
    /// </remarks>
    [Fact]
    public async Task PostgreSql_with_no_connection_string_is_refused()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(overrides: Provider(AlvoHostDatabaseOptions.PostgreSql)));

        refusal.Message.ShouldContain("ConnectionStrings__Alvo");
        refusal.Message.ShouldContain(AlvoHostDatabaseOptions.PostgreSql);
    }

    /// <summary>
    /// A configuration refusal leaves the database exactly as it found it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The schema assertion is the fact, not the throw</b> — the same claim
    /// <c>A_credential_the_startup_validation_refuses_leaves_the_database_untouched</c> makes for a mistyped
    /// dev-key scope, restated for the descriptor path because the two are refused by different validators and
    /// nothing else would notice one of them moving after the boot.
    /// </para>
    /// <para>
    /// It is the difference between a recoverable mistake and an unbootable deployment: a start that committed a
    /// migration and <em>then</em> refused cannot be rolled back, because the previous descriptor is destructive
    /// relative to the schema the failed start already wrote.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_configuration_refusal_leaves_the_database_untouched()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await Should.ThrowAsync<OptionsValidationException>(
                () => AlvoHostWorld.StartAsync(MissingDescriptorPath, databasePath: databasePath));

            AlvoHostWorld.TableNamesIn(databasePath).ShouldBeEmpty(
                "a descriptor path the startup validation refuses must not have run the framework's own DDL "
                + "against the database first — rolling the deployment back does not undo it");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>Two things wrong are two refusals, so the container is fixable in one restart.</summary>
    [Fact]
    public void Every_bad_value_is_reported_at_once_rather_than_one_per_restart()
    {
        var options = new AlvoHostOptions
        {
            DescriptorPath = MissingDescriptorPath,
            Database = new AlvoHostDatabaseOptions { Provider = "mongo" },
        };

        var result = Validation().Validate(null, options);

        result.Failures.ShouldNotBeNull().Count().ShouldBe(2);
    }

    /// <summary>
    /// The unknown-provider arm of the validation, pinned directly.
    /// </summary>
    /// <remarks>
    /// Unreachable through a started host on purpose: <c>AlvoHost.CreateBuilder</c> has to choose the driver
    /// while the container is still being built, so it refuses the name first. Leaving one property of a
    /// validated options type unchecked is how a later composition slips past, so the arm exists — and a fact
    /// written through a host that cannot reach it would pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void The_validation_refuses_an_unknown_provider_even_where_the_driver_selection_cannot_reach_it()
    {
        var options = new AlvoHostOptions
        {
            DescriptorPath = AlvoHostWorld.DescriptorPath(AlvoHostWorld.DefaultDescriptorFileName),
            Database = new AlvoHostDatabaseOptions { Provider = "mongo" },
        };

        var result = Validation().Validate(null, options);

        result.FailureMessage.ShouldNotBeNull().ShouldContain("mongo");
    }

    /// <summary>
    /// A refused start exits <c>78</c> — <c>EX_CONFIG</c> — rather than the crash-shaped code #132 observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through <see cref="AlvoHost.RunAsync"/>, which <em>is</em> the container's <c>Program.cs</c>:
    /// anything else would measure a re-assembly of the entry point rather than the entry point. The
    /// configuration arrives as command-line arguments because that is a source the real process reads, so
    /// nothing about the fixture is special. All three misconfigurations are covered, which is this repository's
    /// answer to #132's second question — the exit is owed to every one of them, not only to the descriptor.
    /// </para>
    /// <para>
    /// The literal <c>78</c> rather than the constant: the exit code is a wire contract with whatever reads
    /// <c>docker inspect</c>, and asserting the constant against itself would let a rename change it silently.
    /// </para>
    /// </remarks>
    /// <param name="misconfiguration">One command-line argument, spelled the way a container's environment is.</param>
    [Theory]
    [InlineData($"--Alvo:DescriptorPath={MissingDescriptorPath}")]
    [InlineData("--Alvo:Database:Provider=mongo")]
    [InlineData($"--Alvo:Database:Provider={AlvoHostDatabaseOptions.PostgreSql}")]
    public async Task A_refused_configuration_exits_seventy_eight_rather_than_crashing(string misconfiguration)
    {
        var exitCode = await AlvoHost.RunAsync([Ephemeral, DescriptorArgument, misconfiguration])
            .WaitAsync(_startMustNotSucceed, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(78);
    }

    /// <summary>
    /// The refusal an operator reads is the whole sentence, not the semicolon-joined summary
    /// <see cref="OptionsValidationException.Message"/> produces for more than one failure.
    /// </summary>
    [Fact]
    public void What_is_written_to_stderr_keeps_every_refusal_readable()
    {
        var refusal = new OptionsValidationException(
            Options.DefaultName, typeof(AlvoHostOptions), ["first refusal", "second refusal"]);

        var described = AlvoHostExit.Describe(refusal);

        described.ShouldContain("first refusal");
        described.ShouldContain("second refusal");
        described.ShouldNotContain("; ");
    }

    /// <summary>
    /// The non-vacuity control for the deliberate exit: it is a named condition, not a general catch.
    /// </summary>
    /// <remarks>
    /// Without this, a version that treated <em>every</em> failure as a misconfiguration would pass every fact
    /// above while swallowing genuine defects — and taking the runtime's own report and crash dump with them.
    /// The boot's own refusal is on the accepting side because its whole purpose is a message written for the
    /// operator reading a container log.
    /// <para>
    /// The <see cref="FileNotFoundException"/> on the rejecting side is the decision about the
    /// time-of-check/time-of-use window, made explicit. It is the very failure #132 observed, and it is
    /// deliberately <em>not</em> recognized: the validation above is what closes it, and a rule that turned any
    /// missing file into a configuration exit would also claim a missing assembly is one.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unrelated_failure_is_not_treated_as_a_misconfiguration()
    {
        AlvoHostExit.IsConfigurationFailure(
            AlvoHostConfiguration.Refuse("a bad option value")).ShouldBeTrue();
        AlvoHostExit.IsConfigurationFailure(
            new AlvoStartupRefusedException("the schema drifted")).ShouldBeTrue();

        AlvoHostExit.IsConfigurationFailure(
            new FileNotFoundException("Could not find file.", MissingDescriptorPath)).ShouldBeFalse();
        AlvoHostExit.IsConfigurationFailure(new InvalidOperationException("a defect")).ShouldBeFalse();
    }

    /// <summary>
    /// Two option types refused at once still exit deliberately, and the operator still reads both
    /// refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape <c>ValidateOnStart</c> takes when more than one registration fails.</b>
    /// <c>StartupValidator.Validate()</c> collects the failures and throws an
    /// <see cref="AggregateException"/> over them — which the predicate above, reading two exact types,
    /// rejected. The host and the identity package are both <c>ValidateOnStart</c> registrations, so one
    /// bootstrap typo is one candidate for exactly that.
    /// </para>
    /// <para>
    /// <b>Every inner exception, not any.</b> An aggregate carrying one genuine defect beside one refusal
    /// is a defect, and must keep the runtime's own report — otherwise this widening would undo the
    /// rejecting half of the fact above for any failure that happened to travel with a refusal. An empty
    /// aggregate is rejected for the same reason: "all of nothing" is vacuously true, and a failure that
    /// named nothing is not a misconfiguration an operator can act on.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_option_types_refused_together_are_still_a_misconfiguration()
    {
        var both = new AggregateException(
            AlvoHostConfiguration.Refuse("the host's own refusal"),
            AlvoHostConfiguration.Refuse("the identity package's refusal"));

        AlvoHostExit.IsConfigurationFailure(both).ShouldBeTrue();
        AlvoHostExit.Describe(both).ShouldContain("the host's own refusal");
        AlvoHostExit.Describe(both).ShouldContain("the identity package's refusal");

        AlvoHostExit.IsConfigurationFailure(new AggregateException(
            AlvoHostConfiguration.Refuse("a refusal"),
            new InvalidOperationException("a defect"))).ShouldBeFalse();
        AlvoHostExit.IsConfigurationFailure(new AggregateException()).ShouldBeFalse();
    }

    /// <summary>
    /// A half-configured bootstrap administrator is a sentence on stderr and a deliberate <c>78</c> — the
    /// claim the wiring commits make, asserted through the entry point a container runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoHost.RunAsync(string[])"/> <em>is</em> <c>Program.cs</c>, so this measures the
    /// shipped process rather than a re-assembly of it — the same choice
    /// <see cref="A_refused_configuration_exits_seventy_eight_rather_than_crashing"/> makes, and the
    /// reason neither spawns a child: a second process would measure the test host's build outputs
    /// rather than the host's own composition.
    /// </para>
    /// <para>
    /// <b>The printed sentence is asserted as well as the code</b>, because the two are different
    /// claims: the code says the operator's deployment tooling can branch, the sentence says the
    /// operator knows which mount to fix. <c>Console.Error</c> is redirected for the call and restored
    /// after; another fact's refusal arriving in the same buffer can only add text, and none of them
    /// names the bootstrap variable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_half_configured_bootstrap_is_read_as_a_sentence_and_exits_seventy_eight()
    {
        using var captured = new StringWriter();
        var stderr = TextWriter.Synchronized(captured);
        var original = Console.Error;
        int exitCode;

        try
        {
            Console.SetError(stderr);
            exitCode = await AlvoHost.RunAsync([Ephemeral, DescriptorArgument, BootstrapEmailOnly])
                .WaitAsync(_startMustNotSucceed, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
        }

        exitCode.ShouldBe(78);
        captured.ToString().ShouldContain("Alvo__Admin__BootstrapPasswordFile");
    }

    /// <summary>An address with no secret beside it — the way half a bootstrap is configured by hand.</summary>
    private const string BootstrapEmailOnly = "--Alvo:Admin:BootstrapEmail=admin@example.test";

    /// <summary>Port zero, so a fact that regressed into a successful start cannot collide with anything.</summary>
    private const string Ephemeral = "--urls=http://127.0.0.1:0";

    private static readonly TimeSpan _startMustNotSucceed = TimeSpan.FromSeconds(60);

    private static string DescriptorArgument =>
        $"--Alvo:DescriptorPath={AlvoHostWorld.DescriptorPath(AlvoHostWorld.DefaultDescriptorFileName)}";

    /// <summary>
    /// The validation over a configuration with no <c>ConnectionStrings</c> entry and no bootstrap
    /// administrator configured — the shape a container that named a provider and nothing else has.
    /// </summary>
    private static AlvoHostOptionsValidation Validation() =>
        new(new ConfigurationBuilder().Build(), Microsoft.Extensions.Options.Options.Create(new AlvoIdentityOptions()));

    private static Dictionary<string, string?> Provider(string provider) =>
        new(StringComparer.Ordinal) { ["Alvo:Database:Provider"] = provider };

    /// <summary>
    /// <b>Every refusal, not the first.</b> A container with three things wrong is three restarts if
    /// only the first is reported, and an operator reading a crash loop cannot tell a second failure
    /// from the same failure again — the reason <c>Failures</c> is an iterator in the first place.
    /// </summary>
    [Fact]
    public void A_bootstrap_configured_three_ways_wrong_reports_all_three()
    {
        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings =>
            {
                settings["Alvo:Admin:BootstrapEmail"] = "not-an-address";
                settings["Alvo:Admin:BootstrapPasswordFile"] = "/nowhere/secret.txt";
                settings["Alvo:Admin:BootstrapPassword"] = "hunter2";
            }));

        var reported = string.Join("\n", failure.Failures);
        reported.ShouldContain("not-an-address");
        reported.ShouldContain("/nowhere/secret.txt");
        reported.ShouldContain("Alvo__Admin__BootstrapPasswordFile");
        failure.Failures.Count().ShouldBe(3);
    }

    [Fact]
    public void An_email_without_a_password_file_is_refused_naming_the_variable()
    {
        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings => settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test"));

        string.Join("\n", failure.Failures).ShouldContain("Alvo__Admin__BootstrapPasswordFile");
    }

    [Fact]
    public void A_password_file_without_an_email_is_refused_naming_the_variable()
    {
        var secret = WriteSecret("Str0ng!Passw0rd");

        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings => settings["Alvo:Admin:BootstrapPasswordFile"] = secret));

        string.Join("\n", failure.Failures).ShouldContain("Alvo__Admin__BootstrapEmail");
    }

    /// <summary>
    /// A password in configuration is refused outright, and the fix names the file key. An environment
    /// variable is readable from a process listing, a crash dump and <c>docker inspect</c>; a mounted
    /// secret file is not, which is the whole reason the option is a path.
    /// </summary>
    [Fact]
    public void An_empty_password_file_is_refused_rather_than_seeding_an_empty_password()
    {
        var secret = WriteSecret("   \n");

        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings =>
            {
                settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test";
                settings["Alvo:Admin:BootstrapPasswordFile"] = secret;
            }));

        string.Join("\n", failure.Failures).ShouldContain(secret);
    }

    /// <summary>
    /// A secret the host cannot read is a <em>refusal</em>, not a stack trace — the #132 failure this
    /// subsystem exists to remove, reintroduced by the one check that reads the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity package deliberately never opens the secret; the host does, to refuse an empty mount.
    /// An unreadable one therefore throws out of <c>Validate</c>, and
    /// <see cref="AlvoHostExit.IsConfigurationFailure"/> recognises neither <see cref="IOException"/> nor
    /// <see cref="UnauthorizedAccessException"/> — so the operator would get a crash-shaped exit for a
    /// mount that is merely mounted wrong.
    /// </para>
    /// <para>
    /// The file is really made unreadable rather than a read being faked: a <see cref="FileShare.None"/>
    /// handle is the one way to do that on every platform this repository's CI matrix runs, and the
    /// contents are valid so a version that fell back to the empty-file refusal fails here too.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_secret_the_host_cannot_open_is_refused_rather_than_escaping_the_validation()
    {
        var secret = WriteSecret("Str0ng!Passw0rd");
        using var exclusive = new FileStream(secret, FileMode.Open, FileAccess.Read, FileShare.None);

        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings =>
            {
                settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test";
                settings["Alvo:Admin:BootstrapPasswordFile"] = secret;
            }));

        var reported = string.Join("\n", failure.Failures);
        reported.ShouldContain(secret);
        reported.ShouldContain("cannot be read");
    }

    /// <summary>
    /// The mount the image actually meets: root-owned and mode <c>0400</c>, which the non-root
    /// <c>USER $APP_UID</c> the Dockerfile sets cannot open.
    /// </summary>
    /// <remarks>
    /// <see cref="UnauthorizedAccessException"/> rather than the <see cref="IOException"/> the fact above
    /// raises, and they are two arms of one <c>catch</c> — a fact for only one of them leaves the other
    /// free to be deleted. Runs only where file modes are enforced against this process: Windows has no
    /// Unix mode at all, and a suite running as root reads a mode-<c>000</c> file happily, so
    /// <see cref="UnixFileModesAreEnforced"/> probes rather than guesses.
    /// </remarks>
    [Fact(
        Skip = "Unix file modes are not enforced against this process (Windows, or running as root).",
        SkipUnless = nameof(UnixFileModesAreEnforced))]
    [UnsupportedOSPlatform("windows")]
    public void A_secret_the_non_root_image_may_not_read_is_refused_by_name()
    {
        var secret = WriteSecret("Str0ng!Passw0rd");
        File.SetUnixFileMode(secret, UnixFileMode.None);

        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings =>
            {
                settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test";
                settings["Alvo:Admin:BootstrapPasswordFile"] = secret;
            }));

        var reported = string.Join("\n", failure.Failures);
        reported.ShouldContain(secret);
        reported.ShouldContain("cannot be read");
    }

    /// <summary>
    /// Whether a mode-<c>000</c> file is really unreadable here, measured rather than inferred from the
    /// platform — root ignores the mode, and a fact that ran there would assert nothing.
    /// </summary>
    public static bool UnixFileModesAreEnforced { get; } = MeasureFileModeEnforcement();

    /// <summary>Writes a file, forbids every mode bit, and reports whether reading it still succeeds.</summary>
    private static bool MeasureFileModeEnforcement()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var probe = WriteSecret("probe");

        try
        {
            File.SetUnixFileMode(probe, UnixFileMode.None);
            File.ReadAllText(probe);
            return false;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(probe);
        }
    }

    [Fact]
    public void A_password_set_directly_in_configuration_is_refused()
    {
        var secret = WriteSecret("Str0ng!Passw0rd");

        var failure = Should.Throw<OptionsValidationException>(
            () => Validate(settings =>
            {
                settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test";
                settings["Alvo:Admin:BootstrapPasswordFile"] = secret;
                settings["Alvo:Admin:BootstrapPassword"] = "hunter2";
            }));

        string.Join("\n", failure.Failures).ShouldContain("Alvo__Admin__BootstrapPasswordFile");
    }

    [Fact]
    public void No_bootstrap_configured_at_all_starts_cleanly()
    {
        Should.NotThrow(() => Validate(_ => { }));
    }

    [Fact]
    public void A_correctly_configured_bootstrap_starts_cleanly()
    {
        var secret = WriteSecret("Str0ng!Passw0rd");

        Should.NotThrow(() => Validate(settings =>
        {
            settings["Alvo:Admin:BootstrapEmail"] = "admin@example.test";
            settings["Alvo:Admin:BootstrapPasswordFile"] = secret;
        }));
    }

    /// <summary>
    /// Resolves a real <see cref="AlvoHostOptions"/> through the same container shape
    /// <see cref="AlvoHost.CreateBuilder"/> composes — <c>AddOptions</c> bound to <c>Alvo</c>,
    /// <see cref="AlvoHostOptionsValidation"/> registered against it, and <c>AddAlvoIdentity</c> beside
    /// it, so <see cref="AlvoIdentityOptionsValidation"/>'s own refusals are reachable through
    /// <see cref="IOptions{TOptions}.Value"/> exactly as they are in the running host. A descriptor path
    /// and a provider are preset so only the bootstrap settings a fact adds can be at fault.
    /// </summary>
    /// <param name="configure">Adds or overrides the bootstrap settings under test.</param>
    private static AlvoHostOptions Validate(Action<IDictionary<string, string?>> configure)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:DescriptorPath"] = AlvoHostWorld.DescriptorPath(AlvoHostWorld.DefaultDescriptorFileName),
            ["Alvo:Database:Provider"] = AlvoHostDatabaseOptions.Sqlite,
        };
        configure(settings);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AlvoHostOptions>().Bind(configuration.GetSection("Alvo"));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoHostOptions>, AlvoHostOptionsValidation>());
        services.AddAlvoIdentity(
            store => store.UseSqlite("Data Source=:memory:"),
            identity => configuration.GetSection(AlvoIdentity.ConfigurationSection).Bind(identity));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AlvoHostOptions>>().Value;
    }

    private static string WriteSecret(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        return path;
    }
}
