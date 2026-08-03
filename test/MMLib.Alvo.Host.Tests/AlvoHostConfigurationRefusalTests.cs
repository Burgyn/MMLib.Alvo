using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Host.Internal;
using MMLib.Alvo.Migrations;

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

    /// <summary>Port zero, so a fact that regressed into a successful start cannot collide with anything.</summary>
    private const string Ephemeral = "--urls=http://127.0.0.1:0";

    private static readonly TimeSpan _startMustNotSucceed = TimeSpan.FromSeconds(60);

    private static string DescriptorArgument =>
        $"--Alvo:DescriptorPath={AlvoHostWorld.DescriptorPath(AlvoHostWorld.DefaultDescriptorFileName)}";

    /// <summary>
    /// The validation over a configuration with no <c>ConnectionStrings</c> entry — the shape a container that
    /// named a provider and nothing else has.
    /// </summary>
    private static AlvoHostOptionsValidation Validation() =>
        new(new ConfigurationBuilder().Build());

    private static Dictionary<string, string?> Provider(string provider) =>
        new(StringComparer.Ordinal) { ["Alvo:Database:Provider"] = provider };
}
