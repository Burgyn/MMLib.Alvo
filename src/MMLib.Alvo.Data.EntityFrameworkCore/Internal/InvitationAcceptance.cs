using MMLib.Alvo.Tenants;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The one acceptance of an invitation (S4 plan D4, spec §6.4), run inside a transaction its caller owns — the tenant
/// store's for the accept route, the account store's for an invited sign-in — so both take exactly the same steps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six steps, in this order.</b> (1) Lock the person's account row (none enabled: <see cref="AcceptInvitationStatus.NoAccount"/>),
/// which serialises one person's concurrent accepts. (2) Read the live invitation (<see cref="InvitationTables.LiveSql"/>;
/// none: <see cref="AcceptInvitationStatus.Dead"/>). (3) An email-bound invitation admits only an account whose email is
/// trusted and equal to it, ignoring case (spec §5.3) — otherwise <see cref="AcceptInvitationStatus.Dead"/>, the same
/// answer. (4) A person already a member gets <see cref="AcceptInvitationStatus.AlreadyMember"/> and consumes nothing.
/// (5) Consume one use with <see cref="InvitationTables.ConsumeSql"/>; matching no row, re-read the membership — a
/// concurrent accept of the person's own may have committed — for <see cref="AcceptInvitationStatus.AlreadyMember"/> or
/// <see cref="AcceptInvitationStatus.Dead"/>. (6) Insert the membership with the invitation's roles, ownership and id.
/// </para>
/// <para>
/// <b>Step 5's predicate is the decision on the use count</b>, never step 2's read: on PostgreSQL the loser of a race for a
/// last use waits on the invitation's row and re-evaluates the predicate against what the winner committed; on SQLite the
/// write gate serialises the two outright. A demotion's sweep (D6) revokes through the same row, so an accept that waits
/// on it finds the invitation revoked.
/// </para>
/// </remarks>
internal static class InvitationAcceptance
{
    /// <summary>Accepts the invitation whose code hashes to <paramref name="codeHash"/> for <paramref name="person"/>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The caller's transaction; only <see cref="AcceptInvitationStatus.Joined"/> wrote anything to commit.</param>
    /// <param name="invitations">The invitation tables of the prefix.</param>
    /// <param name="tenants">The tenant tables of the prefix.</param>
    /// <param name="auth">The account tables of the prefix.</param>
    /// <param name="codeHash">The SHA-256 of the presented code, lowercase hex.</param>
    /// <param name="person">The account accepting.</param>
    /// <param name="now">The instant of the acceptance.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task<AcceptInvitationOutcome> AcceptAsync(
        DbConnection connection, DbTransaction transaction, InvitationTables invitations, TenantTables tenants,
        AuthTables auth, string codeHash, UserId person, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(auth);
        var steps = new Steps(connection, transaction, invitations, tenants, person, now);

        if (!await steps.LockedAsync(ct).ConfigureAwait(false))
        {
            return new AcceptInvitationOutcome(AcceptInvitationStatus.NoAccount, null);
        }

        if (await steps.LiveAsync(codeHash, ct).ConfigureAwait(false) is not { } live
            || !await steps.EmailAdmitsAsync(auth, live.Email, ct).ConfigureAwait(false))
        {
            return Dead;
        }

        if (await steps.MembershipAsync(live.Tenant, ct).ConfigureAwait(false) is { } existing)
        {
            return new AcceptInvitationOutcome(AcceptInvitationStatus.AlreadyMember, existing);
        }

        return await steps.ConsumedAsync(live.Id, ct).ConfigureAwait(false)
            ? new AcceptInvitationOutcome(AcceptInvitationStatus.Joined, await steps.JoinAsync(live, ct).ConfigureAwait(false))
            : await steps.LostAsync(live.Tenant, ct).ConfigureAwait(false);
    }

    private static AcceptInvitationOutcome Dead => new(AcceptInvitationStatus.Dead, null);

    /// <summary>The steps of one acceptance, over one connection and transaction, for one person at one instant.</summary>
    private sealed class Steps(
        DbConnection connection, DbTransaction transaction, InvitationTables invitations, TenantTables tenants,
        UserId person, DateTimeOffset now)
    {
        private readonly string _person = Id(person.Value);

        private readonly string _now = StoredInstant.Text(now.ToUniversalTime());

        /// <summary>Step 1: takes the person's account row lock; <see langword="false"/> when there is no enabled account.</summary>
        internal async Task<bool> LockedAsync(CancellationToken ct) =>
            await ExecuteAsync(tenants.LockAccountSql, command => Bind(command, ("@account_id", _person)), ct).ConfigureAwait(false) == 1;

        /// <summary>Step 2: the live invitation of <paramref name="codeHash"/>, or <see langword="null"/> when it is dead.</summary>
        internal async Task<LiveInvitation?> LiveAsync(string codeHash, CancellationToken ct)
        {
            var found = await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction, invitations.LiveSql,
                command => Bind(command, ("@code_hash", codeHash), ("@now", _now)),
                InvitationTables.ReadLive,
                ct).ConfigureAwait(false);

            return found.Count == 0 ? null : found[0];
        }

        /// <summary>
        /// Step 3: whether the person's account satisfies the invitation's binding — any account when it has none, else
        /// only one whose email is trusted and equal to it, ignoring case.
        /// </summary>
        internal async Task<bool> EmailAdmitsAsync(AuthTables auth, string? bound, CancellationToken ct)
        {
            if (bound is null)
            {
                return true;
            }

            var accounts = await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction, auth.AccountSql,
                command => Bind(command, ("@account_id", _person)),
                reader => (Email: reader.IsDBNull(1) ? null : reader.GetString(1), Trusted: InvitationTables.Flag(reader, 2)),
                ct).ConfigureAwait(false);

            return accounts is [{ Trusted: true, Email: { } email }] && string.Equals(email, bound, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Step 4, and step 5's re-read: the person's membership in <paramref name="tenant"/>, if any.</summary>
        internal async Task<StoredMembership?> MembershipAsync(TenantId tenant, CancellationToken ct)
        {
            var found = await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction, tenants.MembershipSql,
                command => Bind(command, ("@user_id", _person), ("@tenant_id", Id(tenant.Value))),
                reader => TenantTables.ReadMembership(reader),
                ct).ConfigureAwait(false);

            return found.Count == 0 ? null : found[0];
        }

        /// <summary>Step 5: consumes one use with the conditional update; <see langword="false"/> when it matched no row.</summary>
        internal async Task<bool> ConsumedAsync(Guid invitation, CancellationToken ct) =>
            await ExecuteAsync(
                invitations.ConsumeSql, command => Bind(command, ("@id", Id(invitation)), ("@now", _now)), ct).ConfigureAwait(false) == 1;

        /// <summary>
        /// Step 5's loss: the last use went to someone else, or the invitation died meanwhile — unless the person's own
        /// concurrent accept committed, which makes them a member.
        /// </summary>
        internal async Task<AcceptInvitationOutcome> LostAsync(TenantId tenant, CancellationToken ct) =>
            await MembershipAsync(tenant, ct).ConfigureAwait(false) is { } existing
                ? new AcceptInvitationOutcome(AcceptInvitationStatus.AlreadyMember, existing)
                : Dead;

        /// <summary>Step 6: inserts the person's membership with the invitation's roles, ownership and id.</summary>
        internal async Task<StoredMembership> JoinAsync(LiveInvitation live, CancellationToken ct)
        {
            await ExecuteAsync(
                invitations.InsertInvitedMemberSql,
                command => Bind(
                    command,
                    ("@tenant_id", Id(live.Tenant.Value)),
                    ("@user_id", _person),
                    ("@roles", TenantTables.RolesText(live.Roles)),
                    ("@is_owner", live.GrantsOwner ? 1 : 0),
                    ("@now", _now),
                    ("@invitation_id", Id(live.Id))),
                ct).ConfigureAwait(false);

            return new StoredMembership(
                live.Tenant, TenantKind.Shared, live.TenantName, [.. live.Roles], live.GrantsOwner, now.ToUniversalTime());
        }

        private async Task<int> ExecuteAsync(string sql, Action<DbCommand> bind, CancellationToken ct)
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                bind(command);

                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static void Bind(DbCommand command, params (string Name, object Value)[] parameters)
        {
            foreach (var (name, value) in parameters)
            {
                RelationalSqlBatch.AddParameter(command, name, value);
            }
        }

        private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    }
}
