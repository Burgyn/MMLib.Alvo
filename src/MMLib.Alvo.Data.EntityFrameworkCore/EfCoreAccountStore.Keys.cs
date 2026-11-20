using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Secrets;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The key half of the account store: signing and refresh-token keys, sealed with the deployment's secret cipher.</summary>
/// <remarks>
/// <para>
/// <b>Insert-if-absent, then read the winner</b> (S2 D12, Review Focus 2): replicas booting at once each generate a key and
/// <c>INSERT … ON CONFLICT (purpose, generation) DO NOTHING</c> it; whoever committed first stands, and every caller reads
/// that row back in the same transaction — on PostgreSQL the loser's insert waited on the winner's and its read sees the
/// committed row. So a token one replica signs validates on the other.
/// </para>
/// <para>
/// <b>Material is never stored in plaintext</b>: it is sealed with <see cref="SecretCipher.Protect"/> (AES-GCM under the
/// key file <c>ISecretStore</c> uses, spec §5.1) and opened on read. No key file, no key operation — refused by message
/// rather than by a generated fallback, for <see cref="SecretCipher"/>'s own reason.
/// </para>
/// </remarks>
internal sealed partial class EfCoreAccountStore
{
    /// <inheritdoc/>
    public async Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var cipher = KeyCipher();
        var material = cipher.Protect(candidate.Material);

        var stored = await InTransactionAsync(
            (connection, transaction, token) => InsertedOrStandingAsync(connection, transaction, candidate, material, token),
            _ => true,
            ct).ConfigureAwait(false);

        return Opened(stored, cipher);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var cipher = KeyCipher();

        var keys = await ReadAsync(
            _tables.LiveKeysSql,
            command => Bind(command, ("@purpose", purpose), ("@now", Instant(now))),
            ReadKey,
            ct).ConfigureAwait(false);

        return [.. keys.Select(key => Opened(key, cipher))];
    }

    private async Task<StoredKey> InsertedOrStandingAsync(
        DbConnection connection, DbTransaction transaction, KeyCandidate candidate, string material, CancellationToken ct)
    {
        await ExecuteAsync(
            connection, transaction, _tables.InsertKeySql,
            command => Bind(
                command,
                ("@kid", candidate.Kid),
                ("@purpose", candidate.Purpose),
                ("@generation", candidate.Generation),
                ("@material", material),
                ("@now", Instant(candidate.Now))),
            ct).ConfigureAwait(false);

        var standing = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.KeySql,
            command => Bind(command, ("@purpose", candidate.Purpose), ("@generation", candidate.Generation)),
            ReadKey,
            ct).ConfigureAwait(false);

        return standing.Single();
    }

    private static StoredKey Opened(StoredKey sealedKey, SecretCipher cipher) =>
        sealedKey with { Material = cipher.Unprotect(sealedKey.Material) };

    private static StoredKey ReadKey(DbDataReader reader) => new(
        reader.GetString(0),
        Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        StoredInstant.Of(reader.GetString(4)),
        reader.IsDBNull(5) ? null : StoredInstant.Of(reader.GetString(5)));

    /// <summary>The cipher, or the refusal naming the setting this deployment would have to mount.</summary>
    private SecretCipher KeyCipher() => _cipher ?? throw new InvalidOperationException(
        "No secret encryption key is mounted, so this deployment cannot keep the keys end-user sign-in signs with. Point "
        + $"{AlvoSecretOptions.ConfigurationSection}:EncryptionKeyFile at a file holding 32 bytes of base64.");
}
