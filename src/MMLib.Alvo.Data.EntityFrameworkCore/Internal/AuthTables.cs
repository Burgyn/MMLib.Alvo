using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The end-user account tables for one schema prefix — accounts, identities, sessions, refresh tokens and keys — their DDL
/// and every statement <see cref="EfCoreAccountStore"/> runs over them. Identical SQL on both shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>One DDL text per table for both engines</b>: ids and instants are <c>TEXT</c> (instants in
/// <see cref="StoredInstant.Text"/>'s fixed-width UTC form, whose text order is instant order), booleans are
/// <c>INTEGER</c> 0/1. No foreign keys — no framework table carries one; the store's insertion order inside one transaction
/// is what keeps an identity from pointing at no account.
/// </para>
/// <para>
/// <b>The unique constraints are the concurrency story, not decoration.</b> <c>(provider, subject)</c> makes two concurrent
/// first sign-ins of one identity converge on one account; <c>subject_token_id</c> makes an ID token start at most one
/// session; <c>parent_id</c> makes a refresh token have at most one child (both engines admit many <c>NULL</c>s under a
/// <c>UNIQUE</c>, so the family roots do not collide); <c>(purpose, generation)</c> makes two replicas' first keys
/// converge on one. Each is written with <c>ON CONFLICT … DO NOTHING</c> where losing is an expected outcome, so the loser
/// reads the winner instead of aborting its transaction.
/// </para>
/// <para>
/// The bind-parameter names are not in <see cref="PolicyParameterPrefix"/> for the reason <see cref="OutboxTable"/>
/// records: hand-written, single-fragment statements over no entity table.
/// </para>
/// </remarks>
/// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
internal sealed class AuthTables(string schemaPrefix)
{
    /// <summary>Gets the accounts table, e.g. <c>alvo_auth_accounts</c>.</summary>
    internal string Accounts { get; } = schemaPrefix + AlvoFrameworkTables.AuthAccountsSuffix;

    /// <summary>Gets the identities table, e.g. <c>alvo_auth_identities</c>.</summary>
    internal string Identities { get; } = schemaPrefix + AlvoFrameworkTables.AuthIdentitiesSuffix;

    /// <summary>Gets the sessions table, e.g. <c>alvo_auth_sessions</c>.</summary>
    internal string Sessions { get; } = schemaPrefix + AlvoFrameworkTables.AuthSessionsSuffix;

    /// <summary>Gets the refresh-token table, e.g. <c>alvo_auth_refresh_tokens</c>.</summary>
    internal string RefreshTokens { get; } = schemaPrefix + AlvoFrameworkTables.AuthRefreshTokensSuffix;

    /// <summary>Gets the keys table, e.g. <c>alvo_auth_keys</c>.</summary>
    internal string Keys { get; } = schemaPrefix + AlvoFrameworkTables.AuthKeysSuffix;

    /// <summary>Creates the five tables and their indexes if any is missing — on a connection with no transaction in flight.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task EnsureAsync(DbConnection connection, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, Accounts, AccountsDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, Identities, IdentitiesDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, IndexDdl(Identities, "account_id"), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, Sessions, SessionsDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, IndexDdl(Sessions, "account_id"), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, RefreshTokens, RefreshTokensDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, IndexDdl(RefreshTokens, "session_id"), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, Keys, KeysDdl, ct).ConfigureAwait(false);
    }

    /// <summary>Gets the accounts table's DDL.</summary>
    internal string AccountsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Accounts} (
            id TEXT NOT NULL PRIMARY KEY,
            email TEXT NULL,
            email_trusted INTEGER NOT NULL,
            display_name TEXT NULL,
            avatar_url TEXT NULL,
            created_at TEXT NOT NULL,
            last_sign_in_at TEXT NULL,
            disabled_at TEXT NULL
        )
        """;

    /// <summary>Gets the identities table's DDL.</summary>
    internal string IdentitiesDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Identities} (
            provider TEXT NOT NULL,
            subject TEXT NOT NULL,
            account_id TEXT NOT NULL,
            email TEXT NULL,
            email_trusted INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (provider, subject)
        )
        """;

    /// <summary>Gets the sessions table's DDL.</summary>
    internal string SessionsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Sessions} (
            id TEXT NOT NULL PRIMARY KEY,
            account_id TEXT NOT NULL,
            provider TEXT NOT NULL,
            subject_token_id TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            last_refreshed_at TEXT NULL,
            expires_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            revoked_reason TEXT NULL
        )
        """;

    /// <summary>Gets the refresh-token table's DDL.</summary>
    internal string RefreshTokensDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {RefreshTokens} (
            id TEXT NOT NULL PRIMARY KEY,
            session_id TEXT NOT NULL,
            parent_id TEXT NULL UNIQUE,
            created_at TEXT NOT NULL,
            rotated_at TEXT NULL
        )
        """;

    /// <summary>Gets the keys table's DDL.</summary>
    internal string KeysDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Keys} (
            kid TEXT NOT NULL PRIMARY KEY,
            purpose TEXT NOT NULL,
            generation INTEGER NOT NULL,
            material TEXT NOT NULL,
            created_at TEXT NOT NULL,
            retires_at TEXT NULL,
            UNIQUE (purpose, generation)
        )
        """;

    /// <summary>The <c>CREATE INDEX IF NOT EXISTS</c> on one column of one of these tables.</summary>
    /// <param name="table">The validated table name.</param>
    /// <param name="column">The column.</param>
    internal static string IndexDdl(string table, string column) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_{column} ON {table} ({column})";

    /// <summary>Gets the statement reading the account an identity belongs to.</summary>
    internal string FindIdentitySql =>
        $"SELECT account_id FROM {Identities} WHERE provider = @provider AND subject = @subject";

    /// <summary>Gets the statement claiming an identity for a new account; a concurrent first claim makes it write nothing.</summary>
    internal string InsertIdentitySql =>
        $"""
        INSERT INTO {Identities} (provider, subject, account_id, email, email_trusted, created_at)
        VALUES (@provider, @subject, @account_id, @email, @email_trusted, @now)
        ON CONFLICT (provider, subject) DO NOTHING
        """;

    /// <summary>Gets the statement inserting a new account.</summary>
    internal string InsertAccountSql =>
        $"""
        INSERT INTO {Accounts} (id, email, email_trusted, display_name, avatar_url, created_at, last_sign_in_at, disabled_at)
        VALUES (@account_id, @email, @email_trusted, @display_name, @avatar_url, @now, @now, NULL)
        """;

    /// <summary>
    /// Gets the statement refreshing an enabled account's profile at sign-in — never its display name (S2 D8). Matching no
    /// row means the account is disabled.
    /// </summary>
    internal string RefreshProfileSql =>
        $"""
        UPDATE {Accounts} SET email = @email, email_trusted = @email_trusted, avatar_url = @avatar_url, last_sign_in_at = @now
         WHERE id = @account_id AND disabled_at IS NULL
        """;

    /// <summary>Gets the statement refreshing the email an identity last asserted.</summary>
    internal string RefreshIdentitySql =>
        $"UPDATE {Identities} SET email = @email, email_trusted = @email_trusted WHERE provider = @provider AND subject = @subject";

    /// <summary>Gets the statement starting a session; a subject token that already started one makes it write nothing.</summary>
    internal string InsertSessionSql =>
        $"""
        INSERT INTO {Sessions} (id, account_id, provider, subject_token_id, created_at, last_refreshed_at, expires_at, revoked_at, revoked_reason)
        VALUES (@session_id, @account_id, @provider, @subject_token_id, @now, @now, @expires_at, NULL, NULL)
        ON CONFLICT (subject_token_id) DO NOTHING
        """;

    /// <summary>Gets the statement inserting a refresh token — a session's first (no parent) or a rotation's child.</summary>
    internal string InsertRefreshTokenSql =>
        $"""
        INSERT INTO {RefreshTokens} (id, session_id, parent_id, created_at, rotated_at)
        VALUES (@token_id, @session_id, @parent_id, @now, NULL)
        """;

    /// <summary>Gets the statement counting the live sessions of one id on one account (0 or 1).</summary>
    internal string LiveSessionSql =>
        $"""
        SELECT COUNT(*) FROM {Sessions} s JOIN {Accounts} a ON a.id = s.account_id
         WHERE s.id = @session_id AND s.account_id = @account_id AND s.revoked_at IS NULL AND s.expires_at > @now
           AND a.disabled_at IS NULL
        """;

    /// <summary>Gets the statement reading one account.</summary>
    internal string AccountSql =>
        $"SELECT id, email, email_trusted, display_name, avatar_url, created_at FROM {Accounts} WHERE id = @account_id";

    /// <summary>Gets the statement reading the providers of one account's identities, in no order.</summary>
    internal string ProvidersSql => $"SELECT provider FROM {Identities} WHERE account_id = @account_id";

    /// <summary>Gets the statement replacing an account's display name.</summary>
    internal string SetDisplayNameSql => $"UPDATE {Accounts} SET display_name = @display_name WHERE id = @account_id";
}
