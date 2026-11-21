using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.DocsGen.Exchanges;
using MMLib.Alvo.Host;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.DocsGen.Host;

internal static class HostBoot
{
    internal const string ApiKeyHeader = "X-Alvo-Api-Key";

    internal static TimeSpan BootTimeout { get; } = TimeSpan.FromMinutes(2);

    internal static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(30);

    private const int SecretBytes = 24;
    private const string AccountsIssuer = "https://alvo.example/auth/v1";
    private const string AccountsGoogleClientId = "docs.apps.googleusercontent.com";

    internal static async Task<BootedHost> StartAsync(string descriptorPath, IReadOnlyDictionary<string, ExchangeKey> keys, CancellationToken ct, bool withAccounts = false)
    {
        var work = Directory.CreateTempSubdirectory("alvo-docsgen-");
        var secrets = keys.Keys.ToDictionary(id => id, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes)), StringComparer.Ordinal);
        BootedHost? host = null;
        try
        {
            var app = await BuildAsync(Settings(descriptorPath, work.FullName, keys, secrets, withAccounts)).ConfigureAwait(false);
            host = new BootedHost(app, secrets, work);
            await StartWithinTimeoutAsync(host, ct).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await DisposeOrDeleteAsync(host, work).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeOrDeleteAsync(BootedHost? host, DirectoryInfo work)
    {
        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            return;
        }

        DeleteWorkDirectory(work);
    }

    internal static Guid UserOf(string keyId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)).AsSpan(0, 16));

    internal const int DeleteAttempts = 10;

    private static readonly TimeSpan _deleteRetryDelay = TimeSpan.FromMilliseconds(200);

    internal static void DeleteWorkDirectory(DirectoryInfo work) =>
        DeleteWorkDirectory(work, static directory => directory.Delete(recursive: true), _deleteRetryDelay);

    /// <summary>Deletes a booted host's work directory, which holds its SQLite file.</summary>
    /// <remarks>
    /// <para>
    /// <b>Only this host's pool, never <see cref="SqliteConnection.ClearAllPools"/></b> (#547). The DocsGen suites boot
    /// several hosts in parallel, and clearing every pool in the process disposes the SQLite handle of a connection
    /// another host is <em>opening</em> — popped from its pool, not yet bound to its owner — under the thread about to use
    /// it (the reasoning <c>AlvoHostWorld.TryDeleteDatabase</c> records). The pools that hold this file are keyed by the
    /// exact string <see cref="ConnectionStringFor"/> gave the host, so clearing that one releases every pooled handle to it.
    /// </para>
    /// <para>
    /// <b>A bounded retry, because Windows refuses to delete an open file and POSIX does not.</b> A handle can outlive the
    /// call that released it there, and a virus scanner or the search indexer can open a file the moment it was written;
    /// <c>AccountsReferenceTests</c> met exactly that on the Windows leg, intermittently, before and after #547. A holder
    /// that never lets go still fails the run, with the last refusal.
    /// </para>
    /// </remarks>
    /// <param name="work">The work directory.</param>
    /// <param name="delete">One delete attempt; a test's own stands in for the file system's.</param>
    /// <param name="retryDelay">The pause between attempts.</param>
    internal static void DeleteWorkDirectory(DirectoryInfo work, Action<DirectoryInfo> delete, TimeSpan retryDelay)
    {
        ClearThisHostsPool(work);
        for (var attempt = 1; ; attempt++)
        {
            work.Refresh();
            if (!work.Exists)
            {
                return;
            }

            try
            {
                delete(work);
                return;
            }
            catch (IOException) when (attempt < DeleteAttempts)
            {
                Thread.Sleep(retryDelay);
            }
        }
    }

    private static void ClearThisHostsPool(DirectoryInfo work)
    {
        using var connection = new SqliteConnection(ConnectionStringFor(work.FullName));
        SqliteConnection.ClearPool(connection);
    }

    private static string ConnectionStringFor(string workDirectory) => $"Data Source={Path.Combine(workDirectory, "alvo.db")}";

    private static async Task<WebApplication> BuildAsync(Dictionary<string, string?> settings)
    {
        var builder = AlvoHost.CreateBuilder([], configuration => configuration.AddInMemoryCollection(settings));
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        return await AlvoHost.BuildAsync(builder).ConfigureAwait(false);
    }

    private static async Task StartWithinTimeoutAsync(BootedHost host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BootTimeout);
        try
        {
            await host.App.StartAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The in-process Alvo host did not start within {BootTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }
    }

    private static Dictionary<string, string?> Settings(
        string descriptorPath, string workDirectory, IReadOnlyDictionary<string, ExchangeKey> keys, Dictionary<string, string> secrets, bool withAccounts)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:DescriptorPath"] = descriptorPath,
            ["Alvo:Database:Provider"] = "sqlite",
            ["Alvo:Database:SqliteConnectionString"] = ConnectionStringFor(workDirectory),
        };
        var ordinal = 0;
        foreach (var (id, key) in keys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AddKey(settings, $"Alvo:Auth:DevKeys:{ordinal++}", id, key, secrets[id]);
        }

        if (withAccounts)
        {
            AddAccounts(settings, workDirectory);
        }

        return settings;
    }

    private static void AddAccounts(Dictionary<string, string?> settings, string workDirectory)
    {
        var keyFile = Path.Combine(workDirectory, "secrets.key");
        File.WriteAllText(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        settings["Alvo:Accounts:Issuer"] = AccountsIssuer;
        settings["Alvo:Accounts:Providers:Google:ClientIds:0"] = AccountsGoogleClientId;
        settings["Alvo:Secrets:EncryptionKeyFile"] = keyFile;
    }

    private static void AddKey(Dictionary<string, string?> settings, string prefix, string id, ExchangeKey key, string secret)
    {
        settings[$"{prefix}:KeyId"] = id;
        settings[$"{prefix}:Secret"] = secret;
        settings[$"{prefix}:User"] = (key.User ?? UserOf(id)).ToString();
        AddList(settings, $"{prefix}:Roles", key.Roles);
        AddList(settings, $"{prefix}:Scopes", key.Scopes);
        if (key.Tenant is { } tenant)
        {
            settings[$"{prefix}:Tenant"] = tenant.ToString();
        }
    }

    private static void AddList(Dictionary<string, string?> settings, string prefix, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            settings[$"{prefix}:{i.ToString(CultureInfo.InvariantCulture)}"] = values[i];
        }
    }
}

internal sealed class BootedHost : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, string> _secrets;
    private readonly DirectoryInfo _work;
    private bool _disposed;

    internal BootedHost(WebApplication app, IReadOnlyDictionary<string, string> secrets, DirectoryInfo work)
    {
        App = app;
        _secrets = secrets;
        _work = work;
    }

    internal WebApplication App { get; }

    internal HttpClient Client(string? keyId)
    {
        var client = App.GetTestClient();
        client.Timeout = HostBoot.RequestTimeout;
        if (keyId is not null)
        {
            client.DefaultRequestHeaders.Add(HostBoot.ApiKeyHeader, $"{keyId}.{SecretOf(keyId)}");
        }

        return client;
    }

    internal string SecretOf(string keyId) =>
        _secrets.TryGetValue(keyId, out var secret) ? secret : throw new InvalidOperationException($"No key '{keyId}' was booted.");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopQuietlyAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
        HostBoot.DeleteWorkDirectory(_work);
    }

    private async Task StopQuietlyAsync()
    {
        using var timeout = new CancellationTokenSource(HostBoot.BootTimeout);
        try
        {
            await App.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
}
