using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Host;
using MMLib.Alvo.Management;

using System.Security.Cryptography;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// The real standalone host over a temporary SQLite database, booted from <c>examples/bike-workshop</c>.
/// </summary>
/// <remarks>
/// <para>
/// One world serves the whole suite: the assistant only ever dry-runs, so no case can change what the next one reads.
/// </para>
/// <para>
/// The settings are the Host suite's known-good minimum (<c>AlvoHostWorld</c>), except that the dev key's secret is
/// drawn per run: the host listens on a loopback port for as long as the eval runs, and a fixed secret in the
/// repository would be a credential for it.
/// </para>
/// </remarks>
internal sealed class EvalWorld : IAsyncDisposable
{
    internal const string Project = "bike-workshop";
    private const string LoopbackAnyPort = "http://127.0.0.1:0";
    private const string DevKeyId = "alvo-eval";
    private const string DevKeyUser = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private const int DevKeySecretBytes = 32;

    private readonly WebApplication _app;
    private readonly string _database;

    private EvalWorld(WebApplication app, string database)
    {
        _app = app;
        _database = database;
    }

    /// <summary>The running host's management surface — what the assistant's tools read through.</summary>
    internal IAlvoManagement Management => _app.Services.GetRequiredService<IAlvoManagement>();

    /// <summary>Boots the host and applies <c>bike-workshop</c>.</summary>
    /// <param name="repositoryRoot">The repository the example is read from.</param>
    /// <param name="ct">A token to cancel the start.</param>
    internal static async Task<EvalWorld> StartAsync(string repositoryRoot, CancellationToken ct)
    {
        var database = Path.Combine(Path.GetTempPath(), $"alvo-eval-{Guid.NewGuid():N}.db");
        var builder = AlvoHost.CreateBuilder([], configuration => configuration.AddInMemoryCollection(Settings(repositoryRoot, database)));
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackAnyPort);

        var app = await AlvoHost.BuildAsync(builder).ConfigureAwait(false);
        await app.StartAsync(ct).ConfigureAwait(false);
        return new EvalWorld(app, database);
    }

    /// <summary>Publishes an administrator as the ambient caller for the awaited calls that follow.</summary>
    internal void ActAsAdministrator() =>
        _app.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = DevKeyId,
        };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
    }

    private static Dictionary<string, string?> Settings(string repositoryRoot, string database) => new(StringComparer.Ordinal)
    {
        ["Alvo:DescriptorPath"] = Path.Combine(repositoryRoot, "examples", Project, $"{Project}.alvo.json"),
        ["Alvo:Database:Provider"] = "sqlite",
        ["Alvo:Database:SqliteConnectionString"] = $"Data Source={database}",
        ["Alvo:Auth:DevKeys:0:KeyId"] = DevKeyId,
        ["Alvo:Auth:DevKeys:0:Secret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(DevKeySecretBytes)),
        ["Alvo:Auth:DevKeys:0:User"] = DevKeyUser,
        ["Alvo:Auth:DevKeys:0:Roles:0"] = "admin",
        ["Alvo:Auth:DevKeys:0:Roles:1"] = "authenticated",
        ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read",
        ["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write",
    };
}
