using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// <see cref="AlvoSchemaOptions"/> — the one setting that decides whether a boot is allowed to run DDL
/// over a database that already has a schema. Two properties of it are load-bearing: the default is the
/// mode that touches nothing, and a value nobody meant to write is refused at startup rather than read as
/// that default.
/// </summary>
/// <remarks>
/// <para>
/// The second property is not free, and the reason is measured. The configuration binder refuses an
/// unknown enum <em>name</em> itself — <c>"yolo"</c> throws
/// <c>InvalidOperationException: Failed to convert configuration value 'yolo' at 'Alvo:Schema:Startup'</c>
/// — but its message cannot name the modes that would have worked, and it silently accepts an out-of-range
/// <em>number</em>: <c>"42"</c> binds to <c>(AlvoSchemaStartupMode)42</c> with no error at all. So the
/// refusal has to be Alvo's own to be readable, and it has to cover the numeric hole to exist at all.
/// </para>
/// <para>
/// Every refusal fact asserts the three mode names, because the message is the whole deliverable: an
/// operator who mistyped the value needs to be told what to type instead.
/// </para>
/// </remarks>
public class AlvoSchemaOptionsTests
{
    [Fact]
    public void Verify_is_the_default_so_an_embedded_host_never_runs_ddl_it_did_not_ask_for()
    {
        new AlvoSchemaOptions().Startup.ShouldBe(AlvoSchemaStartupMode.Verify);
        new AlvoSchemaOptions().AllowDestructive.ShouldBeFalse();
    }

    /// <summary>
    /// <c>Verify == 0</c> matters: a mis-bound or absent configuration value lands on the safe mode,
    /// exactly as <c>default(Role)</c> lands on anon.
    /// </summary>
    [Fact]
    public void The_default_enum_value_is_the_safe_one()
        => default(AlvoSchemaStartupMode).ShouldBe(AlvoSchemaStartupMode.Verify);

    [Fact]
    public void The_mode_binds_from_configuration_case_insensitively()
        => Resolve(("alvo:schema:startup", "apply")).Startup.ShouldBe(AlvoSchemaStartupMode.Apply);

    [Fact]
    public void Every_mode_name_binds_from_configuration()
    {
        Resolve(("Alvo:Schema:Startup", "Verify")).Startup.ShouldBe(AlvoSchemaStartupMode.Verify);
        Resolve(("Alvo:Schema:Startup", "Apply")).Startup.ShouldBe(AlvoSchemaStartupMode.Apply);
        Resolve(("Alvo:Schema:Startup", "Skip")).Startup.ShouldBe(AlvoSchemaStartupMode.Skip);
    }

    [Fact]
    public void The_destructive_allowance_binds_from_configuration()
        => Resolve(("Alvo:Schema:AllowDestructive", "true")).AllowDestructive.ShouldBeTrue();

    [Fact]
    public void An_absent_section_leaves_the_safe_default()
        => Resolve().Startup.ShouldBe(AlvoSchemaStartupMode.Verify);

    /// <summary>
    /// An empty environment variable is a shell accident, not a choice, so it reads as absent — and
    /// "absent" lands on <see cref="AlvoSchemaStartupMode.Verify"/>, the mode that touches nothing.
    /// </summary>
    [Fact]
    public void A_blank_mode_reads_as_absent_rather_than_as_a_typo()
        => Resolve(("Alvo:Schema:Startup", "  ")).Startup.ShouldBe(AlvoSchemaStartupMode.Verify);

    [Fact]
    public void An_unknown_mode_is_refused_at_startup_naming_the_choices()
    {
        var refusal = ShouldRefuse(("Alvo:Schema:Startup", "yolo"));

        refusal.ShouldContain("yolo");
        refusal.ShouldContain("Alvo:Schema:Startup");
        ShouldNameEveryMode(refusal);
    }

    /// <summary>
    /// The hole the framework binder leaves open: <c>Enum.Parse</c> accepts a bare number, so
    /// <c>"42"</c> binds without error to a mode that does not exist and every later
    /// <c>switch</c> over it would take its default arm.
    /// </summary>
    [Fact]
    public void An_out_of_range_numeric_mode_is_refused_at_startup_naming_the_choices()
    {
        var refusal = ShouldRefuse(("Alvo:Schema:Startup", "42"));

        refusal.ShouldContain("42");
        ShouldNameEveryMode(refusal);
    }

    /// <summary>
    /// The same refusal for a value that never came from configuration at all, so the check cannot be
    /// reduced to "re-read the raw string" and still pass.
    /// </summary>
    [Fact]
    public void An_out_of_range_mode_set_in_code_is_refused_at_startup_naming_the_choices()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        services.Configure<AlvoSchemaOptions>(options => options.Startup = (AlvoSchemaStartupMode)42);

        using var provider = services.BuildServiceProvider();

        ShouldNameEveryMode(Refusal(provider));
    }

    private static AlvoSchemaOptions Resolve(params (string Key, string Value)[] settings)
    {
        using var provider = Build(settings);

        return provider.GetRequiredService<IOptions<AlvoSchemaOptions>>().Value;
    }

    private static string ShouldRefuse(params (string Key, string Value)[] settings)
    {
        using var provider = Build(settings);

        return Refusal(provider);
    }

    private static string Refusal(IServiceProvider provider)
        => Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AlvoSchemaOptions>>().Value).Message;

    private static void ShouldNameEveryMode(string refusal)
    {
        refusal.ShouldContain(nameof(AlvoSchemaStartupMode.Verify));
        refusal.ShouldContain(nameof(AlvoSchemaStartupMode.Apply));
        refusal.ShouldContain(nameof(AlvoSchemaStartupMode.Skip));
    }

    private static ServiceProvider Build((string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlvo();

        return services.BuildServiceProvider();
    }
}
