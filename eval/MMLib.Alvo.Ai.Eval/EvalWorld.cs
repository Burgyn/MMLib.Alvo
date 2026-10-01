using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Host;
using MMLib.Alvo.Management;

using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// The real standalone host over a temporary SQLite database, booted from <c>examples/bike-workshop</c>.
/// </summary>
/// <remarks>
/// <para>
/// One world serves the whole suite. The assistant only ever dry-runs; the one write is the forced case's second
/// operator (D15), a description no case grades, and every turn reads its own starting descriptor.
/// </para>
/// <para>
/// <b>No API key is configured.</b> The eval never calls the host over HTTP — it acts through the in-process principal
/// <see cref="ActAsAdministrator"/> publishes — so the loopback listener the host opens for the run accepts no
/// credential at all, which is the default-deny the host already has without one.
/// </para>
/// </remarks>
internal sealed class EvalWorld : IAsyncDisposable
{
    internal const string Project = "bike-workshop";

    /// <summary>Where <see cref="EditAsAnotherOperatorAsync"/> edits — a member no case grades.</summary>
    internal const string OtherOperatorsEdit = "/entities/bikes/description";

    private const string LoopbackAnyPort = "http://127.0.0.1:0";
    private const string CallerKeyId = "alvo-eval";
    private static readonly string[] _sqliteCompanions = [string.Empty, "-wal", "-shm", "-journal"];

    private readonly WebApplication _app;
    private readonly string _database;

    private EvalWorld(WebApplication app, string database)
    {
        _app = app;
        _database = database;
    }

    /// <summary>The running host's management surface — what the assistant's tools read through.</summary>
    internal IAlvoManagement Management => _app.Services.GetRequiredService<IAlvoManagement>();

    /// <summary>Cancelled when the host begins to stop.</summary>
    /// <remarks>
    /// The host's console lifetime takes Ctrl-C (SIGINT) for itself and stops the application, so the eval's own
    /// <c>CancelKeyPress</c> handler may never run; a run linked to this token stops either way.
    /// </remarks>
    internal CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    /// <summary>Boots the host and applies <c>bike-workshop</c>; on failure, removes what the attempt created.</summary>
    /// <param name="repositoryRoot">The repository the example is read from.</param>
    /// <param name="ct">A token to cancel the start.</param>
    internal static async Task<EvalWorld> StartAsync(string repositoryRoot, CancellationToken ct)
    {
        var database = Path.Combine(Path.GetTempPath(), $"alvo-eval-{Guid.NewGuid():N}.db");
        WebApplication? app = null;
        try
        {
            app = await BuildAsync(repositoryRoot, database).ConfigureAwait(false);
            await app.StartAsync(ct).ConfigureAwait(false);
            return new EvalWorld(app, database);
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }

            DeleteDatabase(database);
            throw;
        }
    }

    /// <summary>Publishes an administrator as the ambient caller for the awaited calls that follow.</summary>
    internal void ActAsAdministrator() =>
        _app.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = CallerKeyId,
        };

    /// <summary>Applies a real, harmless edit as another operator would, moving the project to a new revision.</summary>
    /// <remarks>
    /// A unique text each time, so every run moves the revision. The edit stays in the world: every turn reads its own
    /// starting descriptor, so the cases after it grade against a descriptor that already has it.
    /// </remarks>
    /// <param name="ct">A token to cancel the apply.</param>
    internal async Task EditAsAnotherOperatorAsync(CancellationToken ct)
    {
        var current = await Management.GetDescriptorAsync(Project, ct).ConfigureAwait(false);
        var document = JsonNode.Parse(current.DescriptorJson)!;
        document["entities"]!["bikes"]!["description"] = string.Create(
            CultureInfo.InvariantCulture, $"A customer's bicycle, edited by another operator ({Guid.NewGuid():N}).");
        var applied = await Management.ApplyDescriptorAsync(
            Project,
            new ManagementApplyRequest(document.ToJsonString(), current.Revision, Author: CallerKeyId, Reason: "Another operator applies mid-turn (D15)."),
            ct).ConfigureAwait(false);
        if (applied.Revision == current.Revision)
        {
            throw new InvalidOperationException("The second operator's edit did not move the revision, so the forced case forces nothing.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        DeleteDatabase(_database);
    }

    private static Task<WebApplication> BuildAsync(string repositoryRoot, string database)
    {
        var builder = AlvoHost.CreateBuilder([], configuration => configuration.AddInMemoryCollection(Settings(repositoryRoot, database)));
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackAnyPort);
        return AlvoHost.BuildAsync(builder);
    }

    /// <summary>The database file and every companion SQLite may have left beside it (WAL, shared memory, journal).</summary>
    private static void DeleteDatabase(string database)
    {
        // This file's pool only, the same rule the test worlds follow: one world per process today, but a
        // process-wide clear disposes a connection any other world is in the middle of opening.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in _sqliteCompanions)
        {
            File.Delete(database + suffix);
        }
    }

    private static Dictionary<string, string?> Settings(string repositoryRoot, string database) => new(StringComparer.Ordinal)
    {
        ["Alvo:DescriptorPath"] = Path.Combine(repositoryRoot, "examples", Project, $"{Project}.alvo.json"),
        ["Alvo:Database:Provider"] = "sqlite",
        ["Alvo:Database:SqliteConnectionString"] = $"Data Source={database}",
    };
}
