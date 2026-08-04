using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// One host that maps <b>only</b> <c>MapAlvoHealth</c>, over a real database and a real descriptor, and
/// issues no credential of any kind — so every request it makes is anonymous by construction rather than by
/// omitting a header.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="AlvoApiWorld"/>.</b> That world exists to drive the Data API, so it
/// configures keys, an exception handler and an OpenAPI document; none of it is reachable from a probe, and a
/// readiness fact measured through it would be measuring the Data API's registrations as much as the health
/// endpoints'. What the probes need is the smallest host that has an <see cref="AlvoBootState"/> in it.
/// </para>
/// <para>
/// <b>The boot can be suppressed, and that is the only way two of the three phases are reachable at all.</b> A
/// boot that refuses throws out of <c>StartingAsync</c>, so the server never binds and nothing answers — the
/// strong end of the guarantee, and the reason a fact about a <see cref="AlvoBootPhase.Failed"/> probe response
/// cannot be written against a refused start. Removing the hosted service leaves a running host whose boot
/// published nothing, which is both the fail-closed <see cref="AlvoBootPhase.Pending"/> case an operator can
/// really hit (a host that mapped health and has not finished booting) and the only seat from which a
/// <see cref="AlvoBootPhase.Failed"/> state can be observed over HTTP.
/// </para>
/// <para>
/// The phase is asserted after every start, so a world that booted differently from how it was asked to cannot
/// leave a fact passing for the wrong reason — "readiness is 503" is true of a refused boot and of a boot that
/// never ran, and those are not the same claim.
/// </para>
/// </remarks>
internal sealed class AlvoHealthWorld : IAsyncDisposable
{
    internal const string DescriptorFileName = "tenant-notes.alvo.json";

    private readonly AlvoApiDatabase _database;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private AlvoHealthWorld(AlvoApiDatabase database, WebApplication app)
    {
        _database = database;
        _app = app;
        _client = app.GetTestClient();
    }

    /// <summary>What the boot published — writable by a fact, since two phases have no reachable start.</summary>
    internal AlvoBootState BootState => _app.Services.GetRequiredService<AlvoBootState>();

    /// <summary>
    /// The health-check service itself, for the facts whose claim is about what a check <em>reported</em>
    /// rather than about a response body: a description reaches the log and every publisher, which no HTTP
    /// fact can see.
    /// </summary>
    internal HealthCheckService HealthChecks => _app.Services.GetRequiredService<HealthCheckService>();

    /// <summary>Starts a world.</summary>
    /// <param name="setup">Anything this world is configured differently from the default.</param>
    internal static async Task<AlvoHealthWorld> StartAsync(AlvoHealthWorldSetup? setup = null)
    {
        var database = await SqliteApiEngine.Instance.CreateDatabaseAsync();

        try
        {
            return await StartAsync(database, setup ?? new AlvoHealthWorldSetup());
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts a host whose boot is expected to refuse, and hands back the reason it published.
    /// </summary>
    /// <remarks>
    /// The reason comes out of the product — <c>AlvoBootService</c> catches the failure and records
    /// <c>failure.Message</c> — rather than being composed by a fact, which is what makes a disclosure fact
    /// about it worth anything.
    /// </remarks>
    /// <param name="descriptorFileName">The descriptor to boot from, existing or not.</param>
    /// <param name="register">Anything registered after <c>AddAlvo</c>, e.g. a store that fails.</param>
    internal static async Task<string> ReasonARefusedBootPublishesAsync(
        string descriptorFileName = DescriptorFileName, Action<IServiceCollection>? register = null)
    {
        var database = await SqliteApiEngine.Instance.CreateDatabaseAsync();
        await using (database)
        {
            var builder = Builder(database, descriptorFileName);
            register?.Invoke(builder.Services);

            var app = builder.Build();
            await using (app.ConfigureAwait(false))
            {
                var failure = await Should.ThrowAsync<Exception>(
                    () => app.StartAsync(TestContext.Current.CancellationToken));
                failure.ShouldNotBeOfType<ShouldAssertException>();

                return app.Services.GetRequiredService<AlvoBootState>().Failure.ShouldNotBeNull(
                    "a refused boot must publish the reason it refused, or there is nothing to withhold");
            }
        }
    }

    /// <summary>Sends an anonymous GET and reads everything a fact can assert about the answer.</summary>
    /// <param name="path">The probe path.</param>
    internal async Task<ProbeResponse> ProbeAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        return new ProbeResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            response.Headers.CacheControl?.ToString());
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private static async Task<AlvoHealthWorld> StartAsync(AlvoApiDatabase database, AlvoHealthWorldSetup setup)
    {
        var builder = Builder(database, DescriptorFileName);
        setup.Register?.Invoke(builder.Services);

        if (!setup.RunTheBoot)
        {
            DoNotRunTheBoot(builder.Services);
        }

        var app = builder.Build();
        app.MapAlvoHealth();

        if (setup.MapTheDataApi)
        {
            app.MapAlvoDataApi();
        }

        await app.StartAsync(TestContext.Current.CancellationToken);

        var world = new AlvoHealthWorld(database, app);
        world.EnsureTheBootDidWhatItWasAskedTo(setup.RunTheBoot);

        return world;
    }

    /// <summary>
    /// The host an embedded consumer writes: a provider, a descriptor, and nothing else.
    /// </summary>
    /// <param name="database">The engine's database, registered through the extension a host calls.</param>
    /// <param name="descriptorFileName">The descriptor file's name under <c>descriptors/</c>.</param>
    private static WebApplicationBuilder Builder(AlvoApiDatabase database, string descriptorFileName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAlvo(alvo =>
        {
            database.Use(alvo);
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptorFileName));
        });

        return builder;
    }

    /// <summary>
    /// Removes the boot from the host lifecycle, leaving <see cref="AlvoBootState"/> exactly as
    /// <c>AddAlvo</c> registered it.
    /// </summary>
    /// <remarks>
    /// <c>Single</c> rather than a filtered remove: a registration this stopped matching would silently leave
    /// the boot running, and every fact that asked for a pending world would then measure a booted one.
    /// </remarks>
    /// <param name="services">The collection <c>AddAlvo</c> has already written into.</param>
    private static void DoNotRunTheBoot(IServiceCollection services)
    {
        var boot = services.Single(service =>
            service.ServiceType == typeof(IHostedService)
            && service.ImplementationType == typeof(AlvoBootService));

        services.Remove(boot);
    }

    private void EnsureTheBootDidWhatItWasAskedTo(bool bootWasRun) =>
        BootState.Phase.ShouldBe(
            bootWasRun ? AlvoBootPhase.Ready : AlvoBootPhase.Pending,
            "this world's boot did not end up where the fact asked it to, so whatever the probes answer is "
            + "an answer to a different question");
}

/// <summary>Anything an <see cref="AlvoHealthWorld"/> is configured differently from the default.</summary>
/// <param name="RunTheBoot">
/// Whether Alvo's boot runs at all. <see langword="false"/> leaves the phase
/// <see cref="AlvoBootPhase.Pending"/> over a running server, which is the only seat a
/// <see cref="AlvoBootPhase.Failed"/> probe response can be observed from.
/// </param>
/// <param name="Register">Anything registered after <c>AddAlvo</c> — a second <c>AddAlvo</c>, a health check of the host's own.</param>
/// <param name="MapTheDataApi">
/// Whether the host also maps the Data API. Off by default, because a probe reaches none of it — and on for the
/// one fact that needs it: the Data API's endpoint data source is enumerated through the <em>same</em> composite
/// the probes are matched through, so a source that refuses a schema by throwing takes liveness down with it.
/// </param>
internal sealed record AlvoHealthWorldSetup(
    bool RunTheBoot = true,
    Action<IServiceCollection>? Register = null,
    bool MapTheDataApi = false);

/// <summary>Everything a fact can assert about a probe's answer.</summary>
/// <param name="Status">The status code — the only thing an orchestrator reads.</param>
/// <param name="Body">The body, as the exact text an anonymous caller receives.</param>
/// <param name="CacheControl">The <c>Cache-Control</c> header, or <see langword="null"/> when there is none.</param>
internal sealed record ProbeResponse(HttpStatusCode Status, string Body, string? CacheControl);
