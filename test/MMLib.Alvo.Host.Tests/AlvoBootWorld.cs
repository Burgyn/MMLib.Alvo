using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// One host whose <b>only</b> descriptor apply is <c>AlvoBootService</c>'s — registered through
/// <c>AddAlvo</c>, started over <see cref="TestServer"/>, mapping nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="AlvoHostWorld"/>, and the reason is the fact being measured.</b>
/// <see cref="AlvoHost.BuildAsync"/> still applies the descriptor itself, before the host lifecycle runs at
/// all — that call is Task 10's to delete. A world built through it would therefore reach the boot service
/// with the schema already applied and the policy catalog already primed, so every claim here would be
/// vacuous: the refusal could never be raised (the eager apply throws first), and "the unchanged restart
/// primed" would be satisfied by the eager apply rather than by stage 3. The embedded shape below —
/// <c>AddAlvo(alvo =&gt; alvo.UseSqlite(...).FromDescriptor(...))</c> and nothing else — is what the boot
/// service actually ships for, and it is the shape <see cref="AlvoHost"/> collapses to.
/// </para>
/// <para>
/// No routes are mapped, on purpose. <c>MapAlvoDataApi</c> reads its route literals off the applied schema at
/// <em>map</em> time, which is before any boot has primed anything (Task 6 makes that lazy), so a world that
/// mapped would prove the opposite of what it looked like. Priming is therefore asserted where it is actually
/// consumed — the <see cref="ISchemaRegistry"/> route generation and field validation read — rather than
/// through a request that cannot yet work.
/// </para>
/// </remarks>
internal sealed class AlvoBootWorld : IAsyncDisposable
{
    internal const string DefaultDescriptorFileName = "host-boot.alvo.json";

    internal const string DroppedFieldDescriptorFileName = "host-boot-dropped-field.alvo.json";

    internal const string AddedFieldDescriptorFileName = "host-boot-added-field.alvo.json";

    private readonly WebApplication _app;
    private readonly string? _ownedDatabasePath;
    private readonly BootObservingDescriptorSource _descriptorSource;
    private readonly bool _running;

    private AlvoBootWorld(
        WebApplication app,
        string? ownedDatabasePath,
        BootObservingDescriptorSource descriptorSource,
        Exception? startFailure)
    {
        _app = app;
        _ownedDatabasePath = ownedDatabasePath;
        _descriptorSource = descriptorSource;
        _running = startFailure is null;
        StartFailure = startFailure;
    }

    /// <summary>What <c>StartAsync</c> threw, or <see langword="null"/> when the host started.</summary>
    internal Exception? StartFailure { get; }

    /// <summary>The state the boot published — readable after a refused start too, which is the point.</summary>
    internal AlvoBootState BootState => _app.Services.GetRequiredService<AlvoBootState>();

    /// <summary>
    /// Whether the server could already serve a request at the moment the boot read the descriptor.
    /// </summary>
    /// <remarks>
    /// <see cref="TestServer"/> refuses to hand out a client until <c>IServer.StartAsync</c> has given it the
    /// request pipeline, and <c>IServer.StartAsync</c> is what binds the socket in a real host — so "a client
    /// can be created" is this transport's spelling of "the port is open" (design fact 7 measured the port
    /// itself). Observed from inside stage 0 through the descriptor port, because that is the earliest thing
    /// the boot does: a probe that observed from its own lifecycle hook instead would report the same answer
    /// whichever hook the boot ran in, and prove nothing.
    /// </remarks>
    internal bool ServerWasListeningDuringBoot => _descriptorSource.ServerWasListening;

    /// <summary>How many times anything read the descriptor during this world's lifetime.</summary>
    internal int DescriptorReads => _descriptorSource.Reads;

    /// <summary>
    /// Whether the server is bound <em>now</em>, after the start returned — the question a fact about a refused
    /// start has to ask, since <see cref="ServerWasListeningDuringBoot"/> only reports what the boot itself saw.
    /// </summary>
    internal bool ServerIsListening => BootObservingDescriptorSource.CanServeARequest(_app.Services);

    /// <summary>Every entity the primed schema registry reports — empty when nothing primed.</summary>
    /// <remarks>
    /// The registry is what route generation reads its literals off and what a data port validates field
    /// names against, so an entity appearing here is the operational meaning of "the catalog was primed".
    /// </remarks>
    internal IReadOnlyList<string> PrimedEntities =>
        [.. _app.Services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities.Select(entity => entity.Name)];

    /// <summary>Starts a host and fails the fact if the boot refused.</summary>
    /// <param name="descriptor">The descriptor file name under this project's <c>descriptors/</c> output.</param>
    /// <param name="databasePath">A database the caller owns, so two worlds can be started over one file.</param>
    /// <param name="startup">The startup mode, written into configuration exactly as the container spells it.</param>
    internal static async Task<AlvoBootWorld> StartAsync(
        string descriptor = DefaultDescriptorFileName,
        string? databasePath = null,
        AlvoSchemaStartupMode? startup = null)
    {
        var world = await TryStartAsync(descriptor, databasePath, startup);
        if (world.StartFailure is { } failure)
        {
            await world.DisposeAsync();
            throw failure;
        }

        return world;
    }

    /// <summary>
    /// Starts a host and hands the world back <em>whether or not</em> the boot refused, so a fact about a
    /// refusal can read the state that refusal published and still dispose the application afterwards.
    /// </summary>
    /// <param name="descriptor">The descriptor file name under this project's <c>descriptors/</c> output.</param>
    /// <param name="databasePath">A database the caller owns, so two worlds can be started over one file.</param>
    /// <param name="startup">The startup mode, written into configuration exactly as the container spells it.</param>
    /// <param name="startServicesConcurrently">
    /// Whether the host is configured with <see cref="HostOptions.ServicesStartConcurrently"/>, which is a real
    /// supported knob and the one composition under which a refused boot does <em>not</em> stop the server from
    /// binding.
    /// </param>
    internal static async Task<AlvoBootWorld> TryStartAsync(
        string descriptor = DefaultDescriptorFileName,
        string? databasePath = null,
        AlvoSchemaStartupMode? startup = null,
        bool startServicesConcurrently = false)
    {
        var ownedDatabasePath = databasePath is null ? AlvoHostWorld.TempDatabasePath() : null;
        var builder = Builder(
            descriptor, databasePath ?? ownedDatabasePath!, startup, startServicesConcurrently);
        var app = builder.Build();
        var descriptorSource = (BootObservingDescriptorSource)app.Services.GetRequiredService<IDescriptorSource>();

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new AlvoBootWorld(app, ownedDatabasePath, descriptorSource, startFailure: null);
        }
        catch (Exception failure)
        {
            return new AlvoBootWorld(app, ownedDatabasePath, descriptorSource, failure);
        }
    }

    private static WebApplicationBuilder Builder(
        string descriptor,
        string databasePath,
        AlvoSchemaStartupMode? startup,
        bool startServicesConcurrently = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings(startup));
        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={databasePath}")
            .FromDescriptor(AlvoHostWorld.DescriptorPath(descriptor)));

        if (startServicesConcurrently)
        {
            builder.Services.Configure<HostOptions>(host => host.ServicesStartConcurrently = true);
        }

        ObserveTheDescriptorRead(builder.Services);

        return builder;
    }

    private static Dictionary<string, string?> Settings(AlvoSchemaStartupMode? startup) =>
        startup is null
            ? []
            : new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Alvo:Schema:Startup"] = startup.Value.ToString(),
            };

    /// <summary>
    /// Wraps the descriptor source <c>FromDescriptor</c> registered, rather than replacing it: the production
    /// file source stays in the path and the world only records when it was read.
    /// </summary>
    /// <param name="services">The collection <c>AddAlvo</c> has already written into.</param>
    private static void ObserveTheDescriptorRead(IServiceCollection services)
    {
        var registered = services.Last(service => service.ServiceType == typeof(IDescriptorSource));
        var inner = (IDescriptorSource)registered.ImplementationInstance!;

        services.Remove(registered);
        services.AddSingleton<IDescriptorSource>(
            provider => new BootObservingDescriptorSource(inner, provider));
    }

    public async ValueTask DisposeAsync()
    {
        if (_running)
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
        }

        await _app.DisposeAsync();

        if (_ownedDatabasePath is { } path)
        {
            AlvoHostWorld.TryDeleteDatabase(path);
        }
    }

    /// <summary>
    /// The production descriptor source, plus a record of whether the server could already serve a request
    /// each time the descriptor was read.
    /// </summary>
    /// <param name="inner">The source <c>FromDescriptor</c> registered.</param>
    /// <param name="services">The host's own container, for reaching <see cref="IServer"/> at read time.</param>
    private sealed class BootObservingDescriptorSource(IDescriptorSource inner, IServiceProvider services)
        : IDescriptorSource
    {
        internal bool ServerWasListening { get; private set; }

        internal int Reads { get; private set; }

        public Task<string> LoadAsync(CancellationToken ct = default)
        {
            Reads++;
            ServerWasListening |= CanServeARequest(services);
            return inner.LoadAsync(ct);
        }

        internal static bool CanServeARequest(IServiceProvider services)
        {
            var server = (TestServer)services.GetRequiredService<IServer>();
            try
            {
                server.CreateClient().Dispose();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
