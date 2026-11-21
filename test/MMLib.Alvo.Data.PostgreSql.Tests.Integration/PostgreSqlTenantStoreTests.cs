using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tests.Ef;
using Npgsql;
using Shouldly;
using System.Globalization;
using System.Text.Json;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="TenantStoreFacts"/> — the leg where concurrent creates at the cap really overlap in the
/// engine, since no in-process write gate serialises them, so only the store's row lock keeps them under it — plus the
/// lock-order facts of the hardening design (<c>2026-11-21-accounts-security-hardening-design.md</c> §2.5), which only
/// held row locks can stage.
/// </summary>
public sealed class PostgreSqlTenantStoreTests : TenantStoreFacts, IAsyncLifetime
{
    private const int RaceIterations = 10;

    private const int OtherInvitations = 2000;

    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <summary>
    /// #543's race fact (design §2.5). An owner's removal and her ban both revoke her invitations with an unordered
    /// multi-row <c>UPDATE</c>: the removal's <c>tenant_id</c> predicate walks the index, the ban's <c>created_by</c> one
    /// the heap. After a HOT update the two meet her two rows in opposite orders, so — once a holder releases both rows at
    /// the same instant — each could take one and wait on the other: PostgreSQL's <c>40P01</c>. The staging is asserted,
    /// not assumed (the plans, the heap order), so a planner change cannot make the fact vacuous.
    /// </summary>
    [Fact]
    public async Task An_owner_s_removal_racing_their_ban_never_deadlocks()
    {
        var deadlocks = new List<string>();
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var (store, services) = await StoreAsync();
            var (tenant, ada, bob, first, second) = await StagedAsync(store, services);
            await using var holder = await PostgresRowLocks.HoldAsync(
                services, $"SELECT 1 FROM {Invitations(services)} WHERE id IN ('{first:D}', '{second:D}') FOR UPDATE");

            var removal = store.RemoveMemberAsync(new MemberRemoval(tenant, bob, ada, Start.AddHours(1)), Ct);
            var ban = services.GetRequiredService<IAccountStore>().SetDisabledAsync(ada, disabled: true, Start.AddHours(1), Ct);
            await PostgresRowLocks.UntilWaitingAsync(
                services, 2, "the removal and the ban must both queue behind the held invitations", removal, ban);
            await holder.CommitAsync(Ct);

            var failure = await DeadlockOfAsync(removal, ban);
            if (failure is not null)
            {
                deadlocks.Add($"iteration {iteration}: {failure}");
                continue;
            }

            (await removal).ShouldBe(MemberChangeStatus.Changed, $"iteration {iteration}");
            (await ban).ShouldBeTrue($"iteration {iteration}");
            await EndStateAsync(store, services, tenant, ada, bob, iteration);
        }

        deadlocks.ShouldBeEmpty("an owner's removal and her ban must never deadlock (40P01)");
    }

    /// <summary>
    /// The deterministic half of design §2.5, the fact the fix is judged by: every member write that sweeps a person's
    /// credentials takes that person's account row first (§2.3, "one person, one mutex"). With her account row held, the
    /// write must queue — and while it queues it must hold none of her invitations or tokens.
    /// </summary>
    /// <param name="write">The member write.</param>
    [Theory]
    [InlineData("a removal")]
    [InlineData("a demotion")]
    [InlineData("leaving")]
    public async Task A_member_write_that_sweeps_takes_the_member_s_account_row_before_their_credentials(string write)
    {
        var (store, services) = await StoreAsync();
        var (tenant, ada, bob) = await TwoOwnersAsync(store, services);
        var invitation = await InviteAsync(store, tenant, ada, Start.AddMinutes(5));
        var token = await MintAsync(services, ada, tenant);
        await using var holder = await PostgresRowLocks.HoldAsync(
            services, $"UPDATE {TableOf(services, AlvoFrameworkTables.AuthAccountsSuffix)} SET last_sign_in_at = last_sign_in_at WHERE id = '{ada.Value:D}'");

        var call = write switch
        {
            "a removal" => store.RemoveMemberAsync(Remove(tenant, bob, ada), Ct),
            "a demotion" => store.UpdateMemberAsync(Update(tenant, bob, ada, null, isOwner: false), Ct),
            _ => store.RemoveMemberAsync(Remove(tenant, ada, ada), Ct),
        };
        await PostgresRowLocks.UntilWaitingAsync(
            services, 1, $"{write} swept Ada's credentials without first taking her account row", call);

        (await PostgresRowLocks.FreeAsync(services, $"SELECT 1 FROM {Invitations(services)} WHERE id = '{invitation:D}' FOR UPDATE NOWAIT"))
            .ShouldBeTrue($"{write} holds her invitation while it waits for her account");
        (await PostgresRowLocks.FreeAsync(
            services, $"SELECT 1 FROM {TableOf(services, AlvoFrameworkTables.ApiKeysSuffix)} WHERE key_id = '{token}' FOR UPDATE NOWAIT"))
            .ShouldBeTrue($"{write} holds her token while it waits for her account");
        await holder.CommitAsync(Ct);
        (await call).ShouldBe(MemberChangeStatus.Changed);
    }

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync() => (await _fixture.StartAsync(new SchemaModel([]))).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Ada and Bob own a tenant; Ada created two live invitations into it, the first HOT-updated so that the heap holds the
    /// second before it while the <c>tenant_id</c> index still reaches the first first; then 2000 invitations of strangers
    /// across 200 tenants and an <c>ANALYZE</c>, so the removal's predicate plans an index walk and the ban's a heap scan.
    /// </summary>
    private static async Task<(TenantId Tenant, UserId Ada, UserId Bob, Guid First, Guid Second)> StagedAsync(
        ITenantStore store, IServiceProvider services)
    {
        var (tenant, ada, bob) = await TwoOwnersAsync(store, services);
        var first = await InviteAsync(store, tenant, ada, Start.AddMinutes(5));
        var second = await InviteAsync(store, tenant, ada, Start.AddMinutes(6));
        var table = Invitations(services);
        await PostgresRowLocks.ExecuteAsync(services, $"UPDATE {table} SET uses = uses WHERE id = '{first:D}'");
        await HeapHoldsTheSecondFirstAsync(services, first, second);
        await PostgresRowLocks.ExecuteAsync(services, StrangersInvitationsSql(table));
        await PostgresRowLocks.ExecuteAsync(services, $"ANALYZE {table}");
        await PlansDifferAsync(services, tenant, ada);
        return (tenant, ada, bob, first, second);
    }

    /// <summary>Asserts the HOT update moved the first invitation's live tuple after the second, on the same page.</summary>
    private static async Task HeapHoldsTheSecondFirstAsync(IServiceProvider services, Guid first, Guid second)
    {
        var (firstPage, firstSlot) = await CtidAsync(services, first);
        var (secondPage, secondSlot) = await CtidAsync(services, second);
        firstPage.ShouldBe(secondPage, "staging: the HOT update must stay on the page, or the index and the heap agree");
        firstSlot.ShouldBeGreaterThan(secondSlot, "staging: the first invitation's live tuple must follow the second in the heap");
    }

    private static async Task<(long Page, long Slot)> CtidAsync(IServiceProvider services, Guid invitation)
    {
        var text = (string)(await PostgresRowLocks.ScalarAsync(
            services, $"SELECT ctid::text FROM {Invitations(services)} WHERE id = '{invitation:D}'"))!;
        var parts = text.Trim('(', ')').Split(',');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Asserts the two sweeps plan differently: the removal's walks the <c>tenant_id</c> index, the ban's the heap.</summary>
    private static async Task PlansDifferAsync(IServiceProvider services, TenantId tenant, UserId ada)
    {
        var sweeps = new InvitationTables(Prefix(services));
        var removal = await PlanAsync(services, Literal(sweeps.RevokeCreatedBySql, tenant, ada));
        var ban = await PlanAsync(services, Literal(sweeps.RevokeCreatedByAccountSql, tenant, ada));
        removal.ShouldMatch("Index Scan|Bitmap Heap Scan", "staging: the removal's sweep must walk the tenant_id index");
        ban.ShouldContain("Seq Scan", Case.Sensitive, "staging: the ban's sweep must scan the heap");
    }

    private static async Task<string> PlanAsync(IServiceProvider services, string sql)
    {
        var plan = (string)(await PostgresRowLocks.ScalarAsync(services, "EXPLAIN (FORMAT JSON) " + sql))!;
        using var document = JsonDocument.Parse(plan);
        return document.RootElement.GetRawText();
    }

    private static string Literal(string sql, TenantId tenant, UserId user) => sql
        .Replace("@now", $"'{StoredInstant.Text(Start.AddHours(1))}'", StringComparison.Ordinal)
        .Replace("@tenant_id", $"'{tenant.Value:D}'", StringComparison.Ordinal)
        .Replace("@user_id", $"'{user.Value:D}'", StringComparison.Ordinal);

    private static string StrangersInvitationsSql(string table) =>
        $"""
        INSERT INTO {table} (id, tenant_id, code_hash, roles, grants_owner, email, max_uses, uses, expires_at, created_by, created_at, revoked_at)
        SELECT gen_random_uuid()::text,
               '00000000-0000-4000-8000-' || lpad((g % 200)::text, 12, '0'),
               md5(g::text || clock_timestamp()::text),
               '[]', 0, NULL, 1, 0, '{StoredInstant.Text(Start.AddDays(7))}', gen_random_uuid()::text,
               '{StoredInstant.Text(Start)}', NULL
          FROM generate_series(1, {OtherInvitations}) AS g
        """;

    /// <summary>The <c>40P01</c> either call died of, or <see langword="null"/>; any other failure is rethrown.</summary>
    private static async Task<string?> DeadlockOfAsync(params Task[] calls)
    {
        try
        {
            await Task.WhenAll(calls);
            return null;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return failure.MessageText;
        }
    }

    /// <summary>Whatever order the removal and the ban ran in: Ada is banned and gone, her credentials dead, Bob still owns.</summary>
    private static async Task EndStateAsync(
        ITenantStore store, IServiceProvider services, TenantId tenant, UserId ada, UserId bob, int iteration)
    {
        (await PostgresRowLocks.ScalarAsync(
            services, $"SELECT COUNT(*) FROM {TableOf(services, AlvoFrameworkTables.TenantMembersSuffix)} WHERE tenant_id = '{tenant.Value:D}' AND user_id = '{ada.Value:D}'"))
            .ShouldBe(0L, $"iteration {iteration}: Ada was removed");
        (await PostgresRowLocks.ScalarAsync(
            services, $"SELECT COUNT(*) FROM {Invitations(services)} WHERE created_by = '{ada.Value:D}' AND revoked_at IS NULL"))
            .ShouldBe(0L, $"iteration {iteration}: none of Ada's invitations is live");
        (await PostgresRowLocks.ScalarAsync(
            services, $"SELECT disabled_at IS NOT NULL FROM {TableOf(services, AlvoFrameworkTables.AuthAccountsSuffix)} WHERE id = '{ada.Value:D}'"))
            .ShouldBe(true, $"iteration {iteration}: Ada is banned");
        (await store.FindMembershipAsync(bob, tenant, Ct)).ShouldNotBeNull().IsOwner.ShouldBeTrue($"iteration {iteration}");
    }

    private static async Task<Guid> InviteAsync(ITenantStore store, TenantId tenant, UserId owner, DateTimeOffset now)
    {
        var request = new CreateInvitationRequest(
            Guid.NewGuid(), tenant, owner, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), [], GrantsOwner: false,
            null, 1, now.AddDays(7), now);
        (await store.CreateInvitationAsync(request, Ct)).Status.ShouldBe(MemberChangeStatus.Changed);
        return request.Id;
    }

    private static async Task<string> MintAsync(IServiceProvider services, UserId minter, TenantId tenant)
    {
        var keyId = "alvo_pat_" + Guid.NewGuid().ToString("N");
        var request = new MintTokenRequest(
            keyId, ApiKeyKind.Personal, Guid.NewGuid().ToString("N"), "ci", minter, minter, tenant, [], ["orders:read"],
            Start.AddDays(30), 10, Start.AddMinutes(7));
        (await services.GetRequiredService<IAppTokenStore>().MintAsync(request, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        return keyId;
    }

    private static string Invitations(IServiceProvider services) => TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix);

    private static string Prefix(IServiceProvider services) =>
        TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)[..^AlvoFrameworkTables.TenantInvitationsSuffix.Length];
}
