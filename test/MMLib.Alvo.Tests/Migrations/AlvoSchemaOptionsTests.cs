using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// <see cref="AlvoSchemaOptions"/> — the one setting that decides whether a boot is allowed to run DDL
/// over a database that already has a schema. Three properties of it are load-bearing: a host that says
/// nothing gets <see cref="AlvoSchemaStartupMode.Apply"/>, a value that went <em>missing</em> still lands on
/// <see cref="AlvoSchemaStartupMode.Verify"/>, and a value nobody meant to write is refused at startup rather
/// than read as either.
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
    /// <summary>
    /// Apply is the default, so the loop the product exists for — edit the descriptor, restart, it works —
    /// needs no configuration on the second run either.
    /// </summary>
    /// <remarks>
    /// Exempting initialization from the mode saves only the <em>first</em> run; the run after the first edit
    /// is drift. The destructive allowance is asserted beside it because that is what keeps this default
    /// honest: applying on boot never means discarding data on boot.
    /// </remarks>
    [Fact]
    public void Apply_is_the_default_so_an_edited_descriptor_still_works_on_the_next_restart()
    {
        new AlvoSchemaOptions().Startup.ShouldBe(AlvoSchemaStartupMode.Apply);
        new AlvoSchemaOptions().AllowDestructive.ShouldBeFalse();
    }

    /// <summary>
    /// The enum's zero stays <c>Verify</c> even though the property's default is <c>Apply</c>, so a value that
    /// goes missing lands on the mode that touches nothing.
    /// </summary>
    /// <remarks>
    /// The two are deliberately different and the assertion pins both halves. Zero is where an uninitialized
    /// field, a <c>default(AlvoSchemaStartupMode)</c> or a silent fallback ends up, and losing a value must
    /// never be how a process earns the right to rewrite a schema; the property's initializer is where a host
    /// that <em>chose</em> to say nothing ends up. Reading one off the other — "make the default match the
    /// enum" — would forfeit whichever guarantee it collapsed.
    /// </remarks>
    [Fact]
    public void The_enum_zero_stays_Verify_even_though_the_configured_default_is_Apply()
    {
        default(AlvoSchemaStartupMode).ShouldBe(AlvoSchemaStartupMode.Verify);
        new AlvoSchemaOptions().Startup.ShouldNotBe(default);
    }

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
    public void An_absent_section_leaves_the_configured_default()
        => Resolve().Startup.ShouldBe(AlvoSchemaStartupMode.Apply);

    /// <summary>
    /// An empty environment variable is a shell accident, not a choice, so it reads as absent — and lands
    /// where absent lands, rather than being refused as a typo.
    /// </summary>
    [Fact]
    public void A_blank_mode_reads_as_absent_rather_than_as_a_typo()
        => Resolve(("Alvo:Schema:Startup", "  ")).Startup.ShouldBe(AlvoSchemaStartupMode.Apply);

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
