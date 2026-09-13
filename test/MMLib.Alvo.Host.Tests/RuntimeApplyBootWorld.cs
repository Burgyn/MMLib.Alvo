using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// A <b>dashboard-first</b> host: <c>AddAlvo</c> plus a driver and nothing else — no
/// <c>FromDescriptor</c>, so no <c>IDescriptorSource</c> is registered at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absent call is the whole fixture.</b> Every other boot world in this project registers a descriptor
/// source, which is code-first; this one is the composition the standalone image actually ships for and the
/// one that, before #83, could not start. Nothing here mocks that absence — <c>AddAlvo</c> is simply called
/// without <c>FromDescriptor</c>, exactly as an operator would.
/// </para>
/// <para>
/// <b>A populated database is made by a real code-first boot, not by writing rows.</b>
/// <see cref="SeedAsync"/> starts an <see cref="AlvoBootWorld"/> over the same file and lets it apply, so the
/// stored descriptor this world reads is one <c>RuntimeSchemaWriter</c> wrote in the same transaction as the
/// schema — which is the invariant the runtime boot relies on. A hand-inserted version row would prove the
/// boot can read a row, not that it can resume from what an apply left.
/// </para>
/// </remarks>
internal sealed class RuntimeApplyBootWorld : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string? _ownedDatabasePath;
    private readonly CapturingLoggerProvider _logs;
    private readonly bool _running;

    private RuntimeApplyBootWorld(
        WebApplication app, string? ownedDatabasePath, CapturingLoggerProvider logs, Exception? startFailure)
    {
        _app = app;
        _ownedDatabasePath = ownedDatabasePath;
        _logs = logs;
        _running = startFailure is null;
        StartFailure = startFailure;
    }

    /// <summary>What <c>StartAsync</c> threw, or <see langword="null"/> when the host started.</summary>
    internal Exception? StartFailure { get; }

    /// <summary>The state the boot published — readable after a refused start too, which is the point.</summary>
    internal AlvoBootState BootState => _app.Services.GetRequiredService<AlvoBootState>();

    /// <summary>Every entity the primed schema registry reports — empty when nothing primed.</summary>
    internal IReadOnlyList<string> PrimedEntities =>
        [.. _app.Services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities.Select(entity => entity.Name)];

    /// <summary>Every log record the host wrote, as <c>Level: message</c>.</summary>
    internal IReadOnlyList<string> Logs => _logs.Records;

    /// <summary>
    /// Applies <paramref name="descriptor"/> to <paramref name="databasePath"/> through a real code-first
    /// boot, so a runtime-apply world started over the same file has something to resume from.
    /// </summary>
    /// <param name="databasePath">The database both worlds share.</param>
    /// <param name="descriptor">The descriptor file name under this project's <c>descriptors/</c> output.</param>
    /// <returns>The project name the descriptor declares, which is what the runtime world must be told.</returns>
    internal static async Task<string> SeedAsync(
        string databasePath, string descriptor = AlvoBootWorld.DefaultDescriptorFileName)
    {
        await using var codeFirst = await AlvoBootWorld.StartAsync(descriptor, databasePath);
        codeFirst.BootState.Phase.ShouldBe(
            AlvoBootPhase.Ready, "the seeding boot has to have applied something, or there is nothing to resume from");

        return ProjectNameOf(descriptor);
    }

    /// <summary>
    /// The project a descriptor declares, read from the file rather than from the boot.
    /// </summary>
    /// <remarks>
    /// <c>AlvoBootState</c> deliberately publishes no project name — it exposes the phase, the applied
    /// revision and the failure, which is what a readiness probe needs. Reading the descriptor's own
    /// <c>name</c> is also the more honest source here: it is what an operator would have to type into
    /// <c>Alvo__Schema__Project</c>, so the fixture does exactly what they would.
    /// </remarks>
    /// <param name="descriptor">The descriptor file name under this project's <c>descriptors/</c> output.</param>
    private static string ProjectNameOf(string descriptor)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AlvoHostWorld.DescriptorPath(descriptor)));

        return document.RootElement.GetProperty("name").GetString()!;
    }

    /// <summary>Starts a dashboard-first host and hands the world back whether or not the boot refused.</summary>
    /// <param name="databasePath">The database to boot over; a fresh temp file when null.</param>
    /// <param name="project">
    /// What to write into <c>Alvo:Schema:Project</c>, spelled exactly as the container does. Null writes the
    /// key at all, which is the "configured nothing" composition.
    /// </param>
    /// <param name="descriptorSource">
    /// A descriptor file to ALSO configure, for the one fact about a host that named both and must be refused.
    /// </param>
    internal static async Task<RuntimeApplyBootWorld> TryStartAsync(
        string? databasePath = null, string? project = null, string? descriptorSource = null)
    {
        var ownedDatabasePath = databasePath is null ? AlvoHostWorld.TempDatabasePath() : null;
        var logs = new CapturingLoggerProvider();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);

        if (project is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Alvo:Schema:Project"] = project,
                });
        }

        builder.Services.AddAlvo(alvo =>
        {
            alvo.UseSqlite($"Data Source={databasePath ?? ownedDatabasePath!}");

            if (descriptorSource is not null)
            {
                alvo.FromDescriptor(AlvoHostWorld.DescriptorPath(descriptorSource));
            }
        });

        var app = builder.Build();
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new RuntimeApplyBootWorld(app, ownedDatabasePath, logs, startFailure: null);
        }
        catch (Exception failure)
        {
            return new RuntimeApplyBootWorld(app, ownedDatabasePath, logs, failure);
        }
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
}
