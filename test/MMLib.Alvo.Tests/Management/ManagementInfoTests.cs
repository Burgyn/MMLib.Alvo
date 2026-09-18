using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// <c>GetInfoAsync</c>, the first member of <see cref="IAlvoManagement"/> — what this deployment says it is.
/// </summary>
public class ManagementInfoTests
{
    [Fact]
    public async Task Info_reports_the_mode_the_host_registered()
    {
        var management = Resolve(alvo => alvo.Services.Configure<AlvoOptions>(o => o.Mode = AlvoMode.Embedded));

        var info = await management.GetInfoAsync(TestContext.Current.CancellationToken);

        info.Mode.ShouldBe("embedded");
    }

    /// <summary>
    /// The internal label, when set, wins over the registered mode.
    /// </summary>
    /// <remarks>
    /// <see cref="AlvoManagementOptions.ModeLabel"/> is <see langword="internal"/> deliberately: a public
    /// free-text override would loosen <c>mode</c> from the two values <see cref="ManagementInfo.Mode"/>
    /// documents. Nothing outside this assembly can reach it, so this fact is about a seam rather than about
    /// a knob a host turns.
    /// </remarks>
    [Fact]
    public async Task The_mode_label_wins_over_the_registered_mode()
    {
        var management = Resolve(alvo =>
            alvo.Services.Configure<AlvoManagementOptions>(o => o.ModeLabel = "standalone"));

        var info = await management.GetInfoAsync(TestContext.Current.CancellationToken);

        info.Mode.ShouldBe("standalone", "the label the host set wins over the mode it registered");
    }

    [Fact]
    public async Task Info_reports_the_build_and_the_startup_mode()
    {
        var info = await Resolve(configure: null).GetInfoAsync(TestContext.Current.CancellationToken);

        info.Version.ShouldNotBeNullOrWhiteSpace();
        info.StartupMode.ShouldBe("apply", "AlvoSchemaOptions.Startup defaults to Apply");
    }

    /// <summary>
    /// <b>The management surface activates in a container that has no data port at all</b>, and says so
    /// rather than inventing a driver name.
    /// </summary>
    /// <remarks>
    /// The fixture is the point. <see cref="Resolve"/> registers a schema migrator and no
    /// <see cref="IAlvoData"/>, which is the composition that made
    /// <c>TryAddSingleton&lt;IAlvoManagement, AlvoManagementService&gt;</c> fail to activate at all: a
    /// nullable constructor parameter is not an optional dependency to the container. This is the regression
    /// that guards the factory registration.
    /// </remarks>
    [Fact]
    public async Task The_management_surface_resolves_in_a_container_with_no_data_port()
    {
        var info = await Resolve(configure: null).GetInfoAsync(TestContext.Current.CancellationToken);

        info.DataProvider.ShouldBe(
            "none", "no driver is registered in this fixture, and inventing an engine name would be a lie");
    }

    [Fact]
    public async Task Info_reports_the_registered_driver_when_there_is_one()
    {
        var management = Resolve(alvo => alvo.Services.AddSingleton<IAlvoData>(services => new InMemoryAlvoData(
            services.GetRequiredService<IPolicyEngine>(),
            services.GetRequiredService<IPredicateEvaluator>(),
            new SchemaModel([]))));

        var info = await management.GetInfoAsync(TestContext.Current.CancellationToken);

        info.DataProvider.ShouldBe(
            nameof(InMemoryAlvoData),
            "the core may not reference the adapter that knows an engine's name, so it reports the port's own");
    }

    /// <summary>The management surface of a host registered the way a deployment registers one.</summary>
    /// <remarks>
    /// The migrator is registered because <c>AlvoProviderValidation</c> refuses an <see cref="AlvoOptions"/>
    /// with no database provider at all, and <c>info</c> reads that options type — so a fixture without one
    /// would measure the provider refusal rather than anything about <c>info</c>. It is the migrator alone
    /// rather than a whole driver, which is what leaves <see cref="Data.IAlvoData"/> genuinely absent for
    /// the fact that says so.
    /// </remarks>
    /// <param name="configure">Anything this host is configured differently from the default.</param>
    private static IAlvoManagement Resolve(Action<IAlvoBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo =>
        {
            alvo.Services.AddSingleton<ISchemaMigrator>(new InMemorySchemaMigrator());
            configure?.Invoke(alvo);
        });

        return services.BuildServiceProvider().GetRequiredService<IAlvoManagement>();
    }
}
