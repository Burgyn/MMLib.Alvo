using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Testing.Secrets;

using System.Security.Cryptography;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — this
// project is granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the whole <see cref="SecretStoreContractTests"/> suite against a real SQLite database file, with a
/// real key file and real encryption.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than <c>Mode=Memory</c>, matching <see cref="SqliteOutboxStoreTests"/>: the store's
/// statements compare and return stored <c>TEXT</c>, and a shared-cache in-memory database is the one SQLite
/// configuration a production host never runs.
/// </para>
/// <para>
/// <b>The suite is the point.</b> The store has no tests of its own beyond this: every behaviour it owes —
/// a value read back byte for byte, an unknown name that is absence rather than an error, a second write
/// that replaces the first, a delete that reports whether it removed anything, a listing that carries no
/// values — is a fact the port's own contract states once and every implementation runs.
/// </para>
/// </remarks>
public sealed class SqliteSecretStoreTests : SecretStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-secret-store-tests-{Guid.NewGuid():N}.db");

    private readonly string _keyPath =
        Path.Combine(Path.GetTempPath(), $"alvo-secret-store-key-{Guid.NewGuid():N}");

    protected override ISecretStore CreateStore()
    {
        WriteKeyFile();
        EnsureTable();

        return new EfCoreSecretStore(
            new RelationalConnectionFactory(CreateConnection),
            new AlvoOptions(),
            SecretCipher.FromKeyFile(_keyPath)!,
            TimeProvider.System);
    }

    /// <summary>
    /// What the operator sees in the table is base64, not the value.
    /// </summary>
    /// <remarks>
    /// The one fact the shared contract cannot state, because it is about the storage rather than the port:
    /// a store that satisfied every contract fact and wrote plaintext would be indistinguishable through the
    /// interface, and the whole reason this store exists is that a database dump must not carry the secrets.
    /// </remarks>
    [Fact]
    public async Task The_stored_row_does_not_carry_the_value()
    {
        var store = CreateStore();
        await store.SetAsync(SecretName.Parse("ai.api-key"), "sk-plaintext", TestContext.Current.CancellationToken);

        var connection = CreateConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {SecretsTable.NameFor(new AlvoOptions().SchemaPrefix)}";
            var stored = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

            stored.ShouldNotContain("sk-plaintext");
            Convert.FromBase64String(stored).Length.ShouldBeGreaterThan("sk-plaintext".Length);
        }
    }

    /// <summary>
    /// With no key mounted the store exists and cannot write, rather than failing to be built.
    /// </summary>
    /// <remarks>
    /// The composition every GitOps deployment runs. Registering the store conditionally would have made
    /// <c>AddAlvo</c> itself throw for them — measured, not imagined: it took out 203 tests before the
    /// decision moved from the registration to the store.
    /// </remarks>
    [Fact]
    public async Task With_no_key_mounted_the_store_cannot_write_and_says_which_file_to_mount()
    {
        EnsureTable();
        var store = new EfCoreSecretStore(
            new RelationalConnectionFactory(CreateConnection), new AlvoOptions(), cipher: null, TimeProvider.System);

        store.CanWrite.ShouldBeFalse();
        (await store.GetAsync(SecretName.Parse("k"), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.ListNamesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            async () => await store.SetAsync(SecretName.Parse("k"), "v", TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("EncryptionKeyFile");
    }

    /// <summary>
    /// Pooling is off so that disposing a connection really releases the OS file handle, which is what lets
    /// <see cref="Dispose"/> delete the file instead of leaving one per test behind.
    /// </summary>
    private SqliteConnection CreateConnection() => new($"Data Source={_databasePath};Pooling=False");

    private void WriteKeyFile()
    {
        if (File.Exists(_keyPath))
        {
            return;
        }

        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        File.WriteAllText(_keyPath, Convert.ToBase64String(key) + Environment.NewLine);
    }

    /// <summary>
    /// Creates the table the way the apply path does, rather than lazily inside the store.
    /// </summary>
    /// <remarks>
    /// <see cref="SystemSchemaInitializer"/> owns this DDL in a deployment, and the store deliberately has no
    /// ensure of its own: an ensure per call would be the second statement on the connection the store's
    /// whole design keeps to one.
    /// </remarks>
    private void EnsureTable()
    {
        using var connection = CreateConnection();
        connection.Open();
        SecretsTable.EnsureAsync(
            connection, SecretsTable.NameFor(new AlvoOptions().SchemaPrefix), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _keyPath })
        {
            // Best-effort, matching SqliteOutboxStoreTests: these are temp files, so a stray lock must not
            // fail the test.
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
