using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The relational <see cref="IWritableSecretStore" />: one connection and <b>one statement</b> per call over
/// <see cref="SecretsTable"/>, with every value encrypted before it reaches a row.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement per member is a measured constraint, not a style</b> — the same one
/// <see cref="EfCoreOutboxStore"/> records. Spike Q5 found exactly one shape that breaks a process sharing a
/// SQLite database with a live request path: a transaction that <em>reads</em> and then <em>writes</em>. So a
/// write here is one <c>INSERT … ON CONFLICT DO UPDATE</c> rather than a read followed by an insert or an
/// update, and wrapping two calls in a transaction to be tidy is the edit that would undo it.
/// </para>
/// <para>
/// <b>Registered only when a key file yielded a cipher.</b> There is no store without a key, rather than a
/// store with a default one: §7.1's answer to the bootstrap paradox is that the credential comes from the
/// platform, and a fallback key would make a deployment that lost its mount silently unable to read back
/// what it wrote.
/// </para>
/// <para>
/// <b>Public, with an internal constructor</b>, exactly as <see cref="EfCoreOutboxStore"/> is: the type is
/// part of what a host can see resolve for <see cref="IWritableSecretStore"/>, while what it is built from —
/// <see cref="RelationalConnectionFactory"/> and the cipher — stays the driver's own business.
/// </para>
/// </remarks>
public sealed class EfCoreSecretStore : IWritableSecretStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly SecretCipher _cipher;
    private readonly TimeProvider _time;
    private readonly string _tableName;

    /// <summary>Initializes a new store over one database's secrets table.</summary>
    /// <param name="connections">Creates a fresh connection per call; each is owned and disposed within that call.</param>
    /// <param name="options">Supplies the validated <see cref="AlvoOptions.SchemaPrefix"/> the table is named from.</param>
    /// <param name="cipher">Encrypts a value on the way in and authenticates it on the way out.</param>
    /// <param name="time">The clock a write stamps <c>updated_at</c> from.</param>
    internal EfCoreSecretStore(
        RelationalConnectionFactory connections, AlvoOptions options, SecretCipher cipher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(time);

        _connections = connections;
        _cipher = cipher;
        _time = time;
        _tableName = SecretsTable.NameFor(options.SchemaPrefix);
    }

    /// <inheritdoc/>
    public bool CanWrite => true;

    /// <inheritdoc/>
    public async ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var stored = await SingleValueAsync(SecretsTable.SelectSql(_tableName), name, ct).ConfigureAwait(false);

        return stored is null ? null : _cipher.Unprotect(stored);
    }

    /// <inheritdoc/>
    public async ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = SecretsTable.UpsertSql(_tableName);
                RelationalSqlBatch.AddParameter(command, "@name", name.Value);
                RelationalSqlBatch.AddParameter(command, "@value", _cipher.Protect(value));
                RelationalSqlBatch.AddParameter(command, "@updated_at", StoredInstant.Text(_time.GetUtcNow()));

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = SecretsTable.DeleteSql(_tableName);
                RelationalSqlBatch.AddParameter(command, "@name", name.Value);

                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default)
    {
        var names = new List<SecretName>();

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = SecretsTable.ListNamesSql(_tableName);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        Add(names, reader.GetString(0));
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Adds a stored name to the listing, skipping one this build could not parse.
    /// </summary>
    /// <remarks>
    /// A row written by a newer build whose grammar widened is not a reason for the settings screen to fail;
    /// it is a name this build cannot address, and the listing says so by leaving it out.
    /// </remarks>
    private static void Add(List<SecretName> names, string stored)
    {
        if (SecretName.TryParse(stored, out var name))
        {
            names.Add(name!);
        }
    }

    /// <summary>The one scalar <paramref name="sql"/> returns for <paramref name="name"/>, or null.</summary>
    private async ValueTask<string?> SingleValueAsync(string sql, SecretName name, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                RelationalSqlBatch.AddParameter(command, "@name", name.Value);

                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
        }
    }
}
