using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Tenants;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The account store — accounts, identities, sessions, refresh tokens and keys, the personal tenant a sign-in or a refresh
/// ensures and the one caller read — the same facts on SQLite and on PostgreSQL (accounts spec §5.1, §5.5, §5.6, §5.7,
/// §6.5, D6; S2 plan D2, D8, D12; S3 plan D4, D6). Every instant and id is the fact's own.
/// </summary>
public abstract class AccountStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _session = TimeSpan.FromDays(180);

    private static readonly TimeSpan _grace = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _idle = TimeSpan.FromDays(30);

    private static readonly string[] _authTables =
        ["alvo_auth_accounts", "alvo_auth_identities", "alvo_auth_sessions", "alvo_auth_refresh_tokens", "alvo_auth_keys"];

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"alvo-account-key-{Guid.NewGuid():N}");

    /// <summary>Gets the instant every fact's sign-in starts at.</summary>
    protected static DateTimeOffset Start => _now;

    /// <summary>Gets the running fact's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts one fresh database and returns its host container.</summary>
    /// <param name="configure">Applied to the collection after <c>AddAlvo</c>, before it is built.</param>
    protected abstract Task<IServiceProvider> StartAsync(Action<IServiceCollection>? configure);

    /// <summary>Disposes the engine fixture.</summary>
    protected abstract ValueTask DisposeFixtureAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisposeFixtureAsync();
        if (File.Exists(_keyFile))
        {
            File.Delete(_keyFile);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_first_sign_in_creates_one_account_with_its_identity_and_a_live_session()
    {
        var (store, services) = await StoreAsync();
        var request = SignIn(Identity("sub-1", "ada@gmail.com", name: "Ada"));

        var outcome = await store.SignInAsync(request, Ct);

        outcome.Status.ShouldBe(SignInStatus.Created);
        var account = (await store.GetAccountAsync(outcome.Account!.Value, Ct)).ShouldNotBeNull();
        account.Providers.ShouldBe(["google"]);
        account.DisplayName.ShouldBe("Ada");
        account.Email.ShouldBe("ada@gmail.com");
        account.EmailTrusted.ShouldBeTrue();
        account.CreatedAt.ShouldBe(_now);
        (await IsLiveAsync(store, account.Id, request.SessionId, _now)).ShouldBeTrue();
        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.AuthIdentitiesSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.AuthRefreshTokensSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_sign_in_finds_the_account_by_provider_and_subject_never_by_email()
    {
        var (store, _) = await StoreAsync();
        var first = await store.SignInAsync(SignIn(Identity("sub-1", "ada@gmail.com")), Ct);

        var again = await store.SignInAsync(SignIn(Identity("sub-1", "ada@example.com", trusted: false)), Ct);
        var other = await store.SignInAsync(SignIn(Identity("sub-2", "ada@gmail.com")), Ct);

        again.ShouldBe(new SignInOutcome(SignInStatus.SignedIn, first.Account));
        var account = (await store.GetAccountAsync(first.Account!.Value, Ct)).ShouldNotBeNull();
        account.Email.ShouldBe("ada@example.com", "a later sign-in refreshes the email");
        account.EmailTrusted.ShouldBeFalse();
        other.Status.ShouldBe(SignInStatus.Created, "one email on another subject is another account");
        other.Account.ShouldNotBe(first.Account);
    }

    [Fact]
    public async Task A_later_sign_in_never_overwrites_the_display_name()
    {
        var (store, _) = await StoreAsync();
        var account = (await store.SignInAsync(SignIn(Identity("sub-1", name: "Ada")), Ct)).Account!.Value;
        (await store.SetDisplayNameAsync(account, "Countess", Ct)).ShouldBeTrue();

        await store.SignInAsync(SignIn(Identity("sub-1", name: "Ada Lovelace", picture: "https://img/new")), Ct);

        var view = (await store.GetAccountAsync(account, Ct)).ShouldNotBeNull();
        view.DisplayName.ShouldBe("Countess");
        view.AvatarUrl.ShouldBe("https://img/new", "the avatar is refreshed, the name is not");
    }

    [Fact]
    public async Task An_unknown_account_has_no_view_and_no_display_name_to_set()
    {
        var (store, _) = await StoreAsync();

        (await store.GetAccountAsync(UserId.New(), Ct)).ShouldBeNull();
        (await store.SetDisplayNameAsync(UserId.New(), "Nobody", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_sign_up_writes_nothing()
    {
        var (store, services) = await StoreAsync();

        var outcome = await store.SignInAsync(SignIn(Identity("sub-1")) with { MayCreate = false }, Ct);

        outcome.ShouldBe(new SignInOutcome(SignInStatus.SignUpRefused, null));
        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(0);
        (await CountAsync(services, AlvoFrameworkTables.AuthIdentitiesSuffix)).ShouldBe(0);
        (await CountAsync(services, AlvoFrameworkTables.AuthSessionsSuffix)).ShouldBe(0);
    }

    [Fact]
    public async Task An_existing_account_signs_in_whatever_the_sign_up_policy_says()
    {
        var (store, _) = await StoreAsync();
        var account = (await store.SignInAsync(SignIn(Identity("sub-1")), Ct)).Account;

        var outcome = await store.SignInAsync(SignIn(Identity("sub-1")) with { MayCreate = false }, Ct);

        outcome.ShouldBe(new SignInOutcome(SignInStatus.SignedIn, account));
    }

    [Fact]
    public async Task A_replayed_subject_token_starts_no_second_session_and_creates_no_account()
    {
        var (store, services) = await StoreAsync();
        var first = SignIn(Identity("sub-1"));
        await store.SignInAsync(first, Ct);

        var replay = await store.SignInAsync(SignIn(Identity("sub-2")) with { SubjectTokenId = first.SubjectTokenId }, Ct);

        replay.ShouldBe(new SignInOutcome(SignInStatus.SubjectTokenReplayed, null));
        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.AuthIdentitiesSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.AuthSessionsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.AuthRefreshTokensSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_concurrent_first_sign_ins_of_one_identity_create_one_account()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var subject = "race-" + iteration.ToString(CultureInfo.InvariantCulture);
            var first = SignIn(Identity(subject));
            var second = SignIn(Identity(subject));

            var outcomes = await Task.WhenAll(store.SignInAsync(first, Ct), store.SignInAsync(second, Ct));

            outcomes.Select(outcome => outcome.Status).ShouldBe([SignInStatus.Created, SignInStatus.SignedIn], ignoreOrder: true);
            outcomes[0].Account.ShouldBe(outcomes[1].Account);
            (await IsLiveAsync(store, outcomes[0].Account!.Value, first.SessionId, _now)).ShouldBeTrue();
            (await IsLiveAsync(store, outcomes[0].Account!.Value, second.SessionId, _now)).ShouldBeTrue();
        }

        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(Iterations);
    }

    [Fact]
    public async Task A_first_sign_in_with_a_personal_tenant_creates_it_with_its_owner_in_one_transaction()
    {
        var (store, services) = await StoreAsync();
        var request = PersonalSignIn(Identity("sub-1", name: "Ada"));

        var outcome = await store.SignInAsync(request, Ct);

        outcome.Status.ShouldBe(SignInStatus.Created);
        var personal = (await Tenants(services).FindMembershipAsync(outcome.Account!.Value, null, Ct)).ShouldNotBeNull();
        personal.Tenant.ShouldBe(request.PersonalTenant!.Id);
        personal.Kind.ShouldBe(TenantKind.Personal);
        personal.IsOwner.ShouldBeTrue();
        personal.Roles.ShouldBeEmpty();
        personal.Name.ShouldBe("Ada");
        personal.JoinedAt.ShouldBe(_now);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task A_personal_tenant_of_a_nameless_account_is_called_personal()
    {
        var (store, services) = await StoreAsync();

        var outcome = await store.SignInAsync(PersonalSignIn(Identity("sub-1", name: null)), Ct);

        (await Tenants(services).FindMembershipAsync(outcome.Account!.Value, null, Ct)).ShouldNotBeNull().Name.ShouldBe("Personal");
    }

    [Fact]
    public async Task A_refused_sign_up_creates_no_tenant()
    {
        var (store, services) = await StoreAsync();

        var refused = await store.SignInAsync(PersonalSignIn(Identity("sub-1")) with { MayCreate = false }, Ct);
        var first = PersonalSignIn(Identity("sub-2"));
        await store.SignInAsync(first, Ct);
        var replayed = await store.SignInAsync(PersonalSignIn(Identity("sub-3")) with { SubjectTokenId = first.SubjectTokenId }, Ct);

        refused.Status.ShouldBe(SignInStatus.SignUpRefused);
        replayed.Status.ShouldBe(SignInStatus.SubjectTokenReplayed);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1, "only sub-2's");
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_account_s_sign_in_creates_no_tenant()
    {
        var (store, services) = await StoreAsync();
        var account = (await store.SignInAsync(SignIn(Identity("sub-1")), Ct)).Account!.Value;
        await store.SetDisabledAsync(account, disabled: true, _now, Ct);

        var outcome = await store.SignInAsync(PersonalSignIn(Identity("sub-1")), Ct);

        outcome.Status.ShouldBe(SignInStatus.Disabled);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(0);
    }

    [Fact]
    public async Task A_later_sign_in_creates_a_missing_personal_tenant_once()
    {
        var (store, services) = await StoreAsync();
        var account = (await store.SignInAsync(SignIn(Identity("sub-1", name: "Ada")), Ct)).Account!.Value;
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(0, "no personal tenant was asked for");
        await store.SetDisplayNameAsync(account, "Countess", Ct);

        var second = PersonalSignIn(Identity("sub-1"));
        await store.SignInAsync(second, Ct);
        await store.SignInAsync(PersonalSignIn(Identity("sub-1")), Ct);

        var personal = (await Tenants(services).FindMembershipAsync(account, null, Ct)).ShouldNotBeNull();
        personal.Tenant.ShouldBe(second.PersonalTenant!.Id, "the first sign-in that asked created it; the next kept it");
        personal.Name.ShouldBe("Countess", "named after the account's display name at creation");
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(1);
    }

    /// <summary>Review Focus 4: two devices sign a new person in at once at level 2.</summary>
    [Fact]
    public async Task Two_concurrent_first_sign_ins_create_one_personal_tenant()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var subject = "race-" + iteration.ToString(CultureInfo.InvariantCulture);
            var first = PersonalSignIn(Identity(subject));
            var second = PersonalSignIn(Identity(subject));

            var outcomes = await Task.WhenAll(store.SignInAsync(first, Ct), store.SignInAsync(second, Ct));

            outcomes.Select(outcome => outcome.Status).ShouldBe([SignInStatus.Created, SignInStatus.SignedIn], ignoreOrder: true);
            outcomes[0].Account.ShouldBe(outcomes[1].Account);
            var personal = (await Tenants(services).FindMembershipAsync(outcomes[0].Account!.Value, null, Ct)).ShouldNotBeNull();
            personal.Tenant.ShouldBeOneOf(first.PersonalTenant!.Id, second.PersonalTenant!.Id);
            personal.IsOwner.ShouldBeTrue();
        }

        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(Iterations);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(Iterations);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(Iterations);
    }

    [Fact]
    public async Task A_refresh_creates_a_missing_personal_tenant_and_a_refused_one_does_not()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var personal = new PersonalTenantRequest(new TenantId(Guid.NewGuid()));
        var refused = await store.RefreshAsync(Refresh(Guid.NewGuid(), _now.AddMinutes(1)) with { PersonalTenant = personal }, Ct);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(0);

        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1)) with { PersonalTenant = personal };
        var outcome = await store.RefreshAsync(rotated, Ct);
        var replayed = await store.RefreshAsync(
            Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(1)) with { PersonalTenant = new(new TenantId(Guid.NewGuid())) }, Ct);

        refused.Status.ShouldBe(RefreshStatus.Refused);
        outcome.Status.ShouldBe(RefreshStatus.Rotated);
        replayed.Status.ShouldBe(RefreshStatus.Replayed);
        (await Tenants(services).FindMembershipAsync(account, null, Ct)).ShouldNotBeNull().Tenant.ShouldBe(personal.Id);
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(1);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(1);
    }

    [Fact]
    public async Task A_replayed_refresh_creates_a_missing_personal_tenant()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1));
        await store.RefreshAsync(rotated, Ct);
        var personal = new PersonalTenantRequest(new TenantId(Guid.NewGuid()));

        var replayed = await store.RefreshAsync(Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(1)) with { PersonalTenant = personal }, Ct);

        replayed.Status.ShouldBe(RefreshStatus.Replayed);
        (await Tenants(services).FindMembershipAsync(account, null, Ct)).ShouldNotBeNull().Tenant.ShouldBe(personal.Id);
    }

    [Fact]
    public async Task The_caller_read_answers_the_session_and_the_named_membership()
    {
        var (store, services) = await StoreAsync();
        var signIn = PersonalSignIn(Identity("sub-1"));
        var account = (await store.SignInAsync(signIn, Ct)).Account!.Value;
        var (other, _) = await SignedInAsync(store);
        var shared = new TenantId(Guid.NewGuid());
        var foreign = new TenantId(Guid.NewGuid());
        await Tenants(services).CreateSharedAsync(new CreateTenantRequest(shared, "Acme", account, ["dispatcher"], 5, _now), Ct);
        await Tenants(services).CreateSharedAsync(new CreateTenantRequest(foreign, "Other", other, ["dispatcher"], 5, _now), Ct);

        var named = await ReadAsync(store, account, signIn.SessionId, _now, new MembershipQuery(shared));
        var another = await ReadAsync(store, account, signIn.SessionId, _now, new MembershipQuery(foreign));
        var unknown = await ReadAsync(store, account, signIn.SessionId, _now, new MembershipQuery(new TenantId(Guid.NewGuid())));
        var personal = await ReadAsync(store, account, signIn.SessionId, _now, new MembershipQuery(null));
        var none = await ReadAsync(store, account, signIn.SessionId, _now, null);
        var dead = await ReadAsync(store, account, signIn.SessionId, signIn.SessionExpiresAt, new MembershipQuery(shared));

        named.Live.ShouldBeTrue();
        var membership = named.Membership.ShouldNotBeNull();
        (membership.Tenant, membership.Kind, membership.Name, membership.IsOwner).ShouldBe((shared, TenantKind.Shared, "Acme", true));
        membership.Roles.ShouldBe(["dispatcher"]);
        another.ShouldBe(new CallerRead(true, null));
        unknown.ShouldBe(new CallerRead(true, null));
        personal.Live.ShouldBeTrue();
        personal.Membership.ShouldNotBeNull().Tenant.ShouldBe(signIn.PersonalTenant!.Id);
        none.ShouldBe(new CallerRead(true, null));
        dead.ShouldBe(new CallerRead(false, null), "a dead session answers no membership");
    }

    [Fact]
    public async Task The_caller_read_finds_no_personal_tenant_an_account_does_not_have()
    {
        var (store, _) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var (other, otherSession) = await SignedInAsync(store);

        var personal = await ReadAsync(store, account, session.SessionId, _now, new MembershipQuery(null));
        var crossed = await ReadAsync(store, other, session.SessionId, _now, new MembershipQuery(null));

        personal.ShouldBe(new CallerRead(true, null));
        crossed.ShouldBe(new CallerRead(false, null), "a session read under another account is dead");
        (await IsLiveAsync(store, other, otherSession.SessionId, _now)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_caller_read_is_dead_after_the_session_s_expiry_and_for_another_account()
    {
        var (store, _) = await StoreAsync();
        var request = SignIn(Identity("sub-1"));
        var account = (await store.SignInAsync(request, Ct)).Account!.Value;

        (await IsLiveAsync(store, account, request.SessionId, request.SessionExpiresAt.AddTicks(-1))).ShouldBeTrue();
        (await IsLiveAsync(store, account, request.SessionId, request.SessionExpiresAt)).ShouldBeFalse();
        (await IsLiveAsync(store, UserId.New(), request.SessionId, _now)).ShouldBeFalse();
        (await IsLiveAsync(store, account, Guid.NewGuid(), _now)).ShouldBeFalse();
    }

    [Fact]
    public void The_tables_are_reserved_framework_names() =>
        _authTables.Except(AlvoFrameworkTables.NamesFor("alvo"), StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public async Task A_fresh_refresh_token_rotates_into_one_child()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var refresh = Refresh(session.RefreshTokenId, _now.AddMinutes(1));

        var outcome = await store.RefreshAsync(refresh, Ct);

        outcome.ShouldBe(new RefreshOutcome(RefreshStatus.Rotated, refresh.ChildId, account, session.SessionId, "google"));
        (await ChildrenAsync(services, session.RefreshTokenId)).ShouldBe(1);
        (await IsLiveAsync(store, account, session.SessionId, refresh.Now)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_rotated_token_inside_the_grace_window_answers_the_same_child()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1));
        await store.RefreshAsync(rotated, Ct);

        var first = await store.RefreshAsync(Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(2)), Ct);
        var second = await store.RefreshAsync(Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(5)), Ct);

        first.ShouldBe(new RefreshOutcome(RefreshStatus.Replayed, rotated.ChildId, account, session.SessionId, "google"));
        second.ShouldBe(first);
        (await ChildrenAsync(services, session.RefreshTokenId)).ShouldBe(1, "a replay inside the window never forks the family");
    }

    [Fact]
    public async Task A_rotated_token_after_the_window_revokes_the_session()
    {
        var (store, _) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1));
        await store.RefreshAsync(rotated, Ct);

        var reuse = await store.RefreshAsync(Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(11)), Ct);

        reuse.Status.ShouldBe(RefreshStatus.Reused);
        (await store.RefreshAsync(Refresh(rotated.ChildId, rotated.Now.AddSeconds(12)), Ct)).Status.ShouldBe(RefreshStatus.Refused);
        (await IsLiveAsync(store, account, session.SessionId, rotated.Now.AddSeconds(12))).ShouldBeFalse();
    }

    [Fact]
    public async Task The_window_boundary_is_inclusive()
    {
        var (store, _) = await StoreAsync();
        var (_, session) = await SignedInAsync(store);
        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1));
        await store.RefreshAsync(rotated, Ct);

        var atTheEdge = await store.RefreshAsync(Refresh(session.RefreshTokenId, rotated.Now.AddSeconds(10)), Ct);

        atTheEdge.Status.ShouldBe(RefreshStatus.Replayed);
        atTheEdge.ChildId.ShouldBe(rotated.ChildId);
    }

    [Fact]
    public async Task A_zero_grace_window_revokes_on_any_reuse()
    {
        var (store, _) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var rotated = Refresh(session.RefreshTokenId, _now.AddMinutes(1)) with { Grace = TimeSpan.Zero };
        await store.RefreshAsync(rotated, Ct);

        var reuse = await store.RefreshAsync(rotated with { ChildId = Guid.NewGuid() }, Ct);

        reuse.Status.ShouldBe(RefreshStatus.Reused, "even at the rotation's own instant");
        (await IsLiveAsync(store, account, session.SessionId, rotated.Now)).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_concurrent_refreshes_of_one_token_get_the_same_child()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (account, session) = await SignedInAsync(store);
            var at = _now.AddMinutes(1);

            var outcomes = await Task.WhenAll(
                store.RefreshAsync(Refresh(session.RefreshTokenId, at), Ct),
                store.RefreshAsync(Refresh(session.RefreshTokenId, at), Ct));

            outcomes.Select(outcome => outcome.Status).ShouldBe([RefreshStatus.Rotated, RefreshStatus.Replayed], ignoreOrder: true);
            outcomes[0].ChildId.ShouldNotBeNull();
            outcomes[1].ChildId.ShouldBe(outcomes[0].ChildId);
            (await ChildrenAsync(services, session.RefreshTokenId)).ShouldBe(1);
            (await IsLiveAsync(store, account, session.SessionId, at)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task An_idle_token_is_refused()
    {
        var (store, _) = await StoreAsync();
        var (_, session) = await SignedInAsync(store);
        var (_, fresh) = await SignedInAsync(store);

        var idle = await store.RefreshAsync(Refresh(session.RefreshTokenId, _now + _idle), Ct);
        var justInTime = await store.RefreshAsync(Refresh(fresh.RefreshTokenId, _now + _idle - TimeSpan.FromTicks(1)), Ct);

        idle.ShouldBe(new RefreshOutcome(RefreshStatus.Refused, null, null, null, null));
        justInTime.Status.ShouldBe(RefreshStatus.Rotated);
    }

    [Fact]
    public async Task An_unknown_token_and_an_expired_session_are_refused()
    {
        var (store, _) = await StoreAsync();
        var (_, session) = await SignedInAsync(store);

        (await store.RefreshAsync(Refresh(Guid.NewGuid(), _now), Ct)).Status.ShouldBe(RefreshStatus.Refused);
        (await store.RefreshAsync(Refresh(session.RefreshTokenId, session.SessionExpiresAt) with { Idle = _session + _session }, Ct))
            .Status.ShouldBe(RefreshStatus.Refused);
    }

    [Fact]
    public async Task A_disabled_account_s_tokens_are_refused_and_its_sessions_revoked()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);

        (await store.SetDisabledAsync(account, disabled: true, _now.AddMinutes(1), Ct)).ShouldBeTrue();

        (await store.RefreshAsync(Refresh(session.RefreshTokenId, _now.AddMinutes(2)), Ct)).Status.ShouldBe(RefreshStatus.Refused);
        (await IsLiveAsync(store, account, session.SessionId, _now.AddMinutes(2))).ShouldBeFalse();
        (await RevokedReasonAsync(services, session.SessionId)).ShouldBe(SessionRevocations.Disabled);
        (await store.SignInAsync(SignIn(session.Identity), Ct)).ShouldBe(new SignInOutcome(SignInStatus.Disabled, null));
        (await CountAsync(services, AlvoFrameworkTables.AuthSessionsSuffix)).ShouldBe(1, "a refused sign-in starts nothing");
        (await store.SetDisabledAsync(UserId.New(), disabled: true, _now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Enabling_an_account_leaves_its_old_sessions_revoked()
    {
        var (store, _) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        await store.SetDisabledAsync(account, disabled: true, _now.AddMinutes(1), Ct);

        (await store.SetDisabledAsync(account, disabled: false, _now.AddMinutes(2), Ct)).ShouldBeTrue();

        (await IsLiveAsync(store, account, session.SessionId, _now.AddMinutes(3))).ShouldBeFalse();
        (await store.RefreshAsync(Refresh(session.RefreshTokenId, _now.AddMinutes(3)), Ct)).Status.ShouldBe(RefreshStatus.Refused);
        var again = SignIn(session.Identity);
        (await store.SignInAsync(again, Ct)).ShouldBe(new SignInOutcome(SignInStatus.SignedIn, account));
        (await IsLiveAsync(store, account, again.SessionId, _now)).ShouldBeTrue();
    }

    /// <summary>
    /// Enabling revokes every session the account still has (spec §5.6), not only those the ban revoked: a session that
    /// reached the table after the ban's revocation — the window the PostgreSQL leg stages with a held transaction — does
    /// not come back to life with the account.
    /// </summary>
    [Fact]
    public async Task Enabling_an_account_revokes_a_session_the_ban_never_saw()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        await store.SetDisabledAsync(account, disabled: true, _now.AddMinutes(1), Ct);
        var late = Guid.NewGuid();
        await ScalarAsync(services, LateSessionSql(services, session.SessionId, late));

        (await store.SetDisabledAsync(account, disabled: false, _now.AddMinutes(2), Ct)).ShouldBeTrue();

        (await IsLiveAsync(store, account, late, _now.AddMinutes(3))).ShouldBeFalse();
        (await RevokedReasonAsync(services, late)).ShouldBe(SessionRevocations.Disabled, "it ends as the ban's");
    }

    /// <summary>
    /// The clean slate is the end of a ban, not a side effect of the operation: <c>{"disabled": false}</c> on an account
    /// that was never disabled (or was enabled already) changes nothing and logs nobody out — and is still not a 404.
    /// </summary>
    [Fact]
    public async Task Enabling_an_account_that_is_not_disabled_keeps_its_sessions()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);

        (await store.SetDisabledAsync(account, disabled: false, _now.AddMinutes(1), Ct)).ShouldBeTrue();
        (await store.SetDisabledAsync(account, disabled: false, _now.AddMinutes(2), Ct)).ShouldBeTrue();

        (await IsLiveAsync(store, account, session.SessionId, _now.AddMinutes(3))).ShouldBeTrue();
        (await RevokedReasonAsync(services, session.SessionId)).ShouldBeNull();
        (await store.RefreshAsync(Refresh(session.RefreshTokenId, _now.AddMinutes(3)), Ct)).Status.ShouldBe(RefreshStatus.Rotated);
        (await store.SetDisabledAsync(UserId.New(), disabled: false, _now, Ct)).ShouldBeFalse("an unknown account is still unknown");
    }

    [Fact]
    public async Task Signing_out_twice_is_the_same_as_once()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);

        (await store.RevokeSessionAsync(session.SessionId, SessionRevocations.Logout, _now.AddMinutes(1), Ct)).ShouldBeTrue();
        (await store.RevokeSessionAsync(session.SessionId, SessionRevocations.Reuse, _now.AddMinutes(2), Ct)).ShouldBeTrue();

        (await RevokedReasonAsync(services, session.SessionId)).ShouldBe(SessionRevocations.Logout, "the first revocation stands");
        (await IsLiveAsync(store, account, session.SessionId, _now.AddMinutes(3))).ShouldBeFalse();
        (await store.RefreshAsync(Refresh(session.RefreshTokenId, _now.AddMinutes(3)), Ct)).Status.ShouldBe(RefreshStatus.Refused);
        (await store.RevokeSessionAsync(Guid.NewGuid(), SessionRevocations.Logout, _now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_concurrent_first_keys_converge_on_one()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var purpose = "signing-" + iteration.ToString(CultureInfo.InvariantCulture);

            var keys = await Task.WhenAll(
                store.EnsureKeyAsync(Candidate(purpose, "kid-a-" + iteration, "material-a"), Ct),
                store.EnsureKeyAsync(Candidate(purpose, "kid-b-" + iteration, "material-b"), Ct));

            keys[1].Kid.ShouldBe(keys[0].Kid);
            keys[1].Material.ShouldBe(keys[0].Material);
            (await store.ReadKeysAsync(purpose, _now, Ct)).ShouldHaveSingleItem().Kid.ShouldBe(keys[0].Kid);
        }

        (await CountAsync(services, AlvoFrameworkTables.AuthKeysSuffix)).ShouldBe(Iterations);
    }

    [Fact]
    public async Task A_key_s_material_is_stored_encrypted()
    {
        var (store, services) = await StoreAsync();

        var stored = await store.EnsureKeyAsync(Candidate(AccountKeyPurposes.Signing, "kid-1", "-----PRIVATE KEY-----"), Ct);

        stored.ShouldBe(new StoredKey(AccountKeyPurposes.Signing, 1, "kid-1", "-----PRIVATE KEY-----", _now, null));
        var raw = (string)(await ScalarAsync(services, $"SELECT material FROM {TableOf(services, AlvoFrameworkTables.AuthKeysSuffix)}"))!;
        raw.ShouldNotContain("PRIVATE KEY");
        (await store.ReadKeysAsync(AccountKeyPurposes.Signing, _now, Ct)).ShouldBe([stored]);
    }

    [Fact]
    public async Task A_retired_key_is_not_read()
    {
        var (store, services) = await StoreAsync();
        await store.EnsureKeyAsync(Candidate(AccountKeyPurposes.Refresh, "kid-1", "hmac"), Ct);
        await ScalarAsync(
            services,
            $"UPDATE {TableOf(services, AlvoFrameworkTables.AuthKeysSuffix)} SET retires_at = '2026-11-13T10:00:00.0000000+00:00'");

        (await store.ReadKeysAsync(AccountKeyPurposes.Refresh, _now.AddMinutes(59), Ct)).ShouldHaveSingleItem();
        (await store.ReadKeysAsync(AccountKeyPurposes.Refresh, _now.AddHours(1), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_key_file_key_operations_are_refused_naming_the_setting()
    {
        var (store, _) = await StoreAsync(withKey: false);

        var ensure = await Should.ThrowAsync<InvalidOperationException>(
            () => store.EnsureKeyAsync(Candidate(AccountKeyPurposes.Signing, "kid-1", "material"), Ct));
        var read = await Should.ThrowAsync<InvalidOperationException>(() => store.ReadKeysAsync(AccountKeyPurposes.Signing, _now, Ct));

        ensure.Message.ShouldContain("Alvo:Secrets:EncryptionKeyFile");
        read.Message.ShouldContain("Alvo:Secrets:EncryptionKeyFile");
    }

    [Fact]
    public void Key_records_never_print_their_material()
    {
        var candidate = Candidate(AccountKeyPurposes.Signing, "kid-1", "s3cret-material");
        var stored = new StoredKey(AccountKeyPurposes.Signing, 1, "kid-1", "s3cret-material", _now, null);

        candidate.ToString().ShouldNotContain("s3cret-material");
        stored.ToString().ShouldNotContain("s3cret-material");
        stored.ToString().ShouldContain("kid-1");
    }

    /// <summary>Signs a fresh identity in and returns its account and the request that started its session.</summary>
    /// <param name="store">The store under test.</param>
    private protected static async Task<(UserId Account, SignInRequest Session)> SignedInAsync(IAccountStore store)
    {
        var request = SignIn(Identity("sub-" + Guid.NewGuid().ToString("N")));
        var outcome = await store.SignInAsync(request, Ct);
        return (outcome.Account!.Value, request);
    }

    /// <summary>A presentation of <paramref name="token"/> at <paramref name="now"/> with a fresh child id and no personal tenant.</summary>
    /// <param name="token">The refresh token id.</param>
    /// <param name="now">The instant of the presentation.</param>
    private protected static RefreshRequest Refresh(Guid token, DateTimeOffset now) =>
        new(token, Guid.NewGuid(), now, _grace, _idle, PersonalTenant: null);

    /// <summary>Whether the session is live: the caller read with no membership.</summary>
    /// <param name="store">The store under test.</param>
    /// <param name="account">The account the token names.</param>
    /// <param name="session">The session the token names.</param>
    /// <param name="now">The instant to judge expiry at.</param>
    private protected static async Task<bool> IsLiveAsync(IAccountStore store, UserId account, Guid session, DateTimeOffset now) =>
        (await ReadAsync(store, account, session, now, null)).Live;

    /// <summary>One caller read.</summary>
    /// <param name="store">The store under test.</param>
    /// <param name="account">The account the token names.</param>
    /// <param name="session">The session the token names.</param>
    /// <param name="now">The instant to judge expiry at.</param>
    /// <param name="membership">The membership to read, if any.</param>
    private protected static Task<CallerRead> ReadAsync(
        IAccountStore store, UserId account, Guid session, DateTimeOffset now, MembershipQuery? membership) =>
        store.ReadCallerAsync(new CallerQuery(account, session, now, membership), Ct);

    private static ITenantStore Tenants(IServiceProvider services) => services.GetRequiredService<ITenantStore>();

    private static KeyCandidate Candidate(string purpose, string kid, string material) => new(purpose, 1, kid, material, _now);

    /// <summary>How many children <paramref name="parent"/> has.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="parent">The refresh token id.</param>
    protected static async Task<long> ChildrenAsync(IServiceProvider services, Guid parent) => Convert.ToInt64(
        await ScalarAsync(
            services,
            $"SELECT COUNT(*) FROM {TableOf(services, AlvoFrameworkTables.AuthRefreshTokensSuffix)} WHERE parent_id = '{parent:D}'"),
        CultureInfo.InvariantCulture);

    /// <summary>
    /// The statement inserting session <paramref name="late"/> as a copy of <paramref name="session"/>, unrevoked — a
    /// sign-in of the same account that commits whenever the fact runs it.
    /// </summary>
    /// <param name="services">The host container.</param>
    /// <param name="session">The session whose account, provider and stamps the copy takes.</param>
    /// <param name="late">The new session's id.</param>
    protected static string LateSessionSql(IServiceProvider services, Guid session, Guid late)
    {
        var sessions = TableOf(services, AlvoFrameworkTables.AuthSessionsSuffix);
        return $"""
            INSERT INTO {sessions} (id, account_id, provider, subject_token_id, created_at, last_refreshed_at, expires_at, revoked_at, revoked_reason)
            SELECT '{late:D}', account_id, provider, '{late:N}', created_at, last_refreshed_at, expires_at, NULL, NULL
              FROM {sessions} WHERE id = '{session:D}'
            """;
    }

    /// <summary>The reason <paramref name="session"/> was revoked with, or <see langword="null"/> while it is live.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="session">The session id.</param>
    protected static async Task<string?> RevokedReasonAsync(IServiceProvider services, Guid session) => await ScalarAsync(
        services,
        $"SELECT revoked_reason FROM {TableOf(services, AlvoFrameworkTables.AuthSessionsSuffix)} WHERE id = '{session:D}'") as string;

    /// <summary>Starts a fresh database and returns its account store and container.</summary>
    /// <param name="withKey">Whether the secrets key file is mounted.</param>
    private protected async Task<(IAccountStore Store, IServiceProvider Services)> StoreAsync(bool withKey = true)
    {
        var services = await StartAsync(withKey ? MountKey : null);
        return (services.GetRequiredService<IAccountStore>(), services);
    }

    private void MountKey(IServiceCollection services)
    {
        if (!File.Exists(_keyFile))
        {
            File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        services.Configure<AlvoSecretOptions>(options => options.EncryptionKeyFile = _keyFile);
    }

    private static VerifiedIdentity Identity(
        string subject, string? email = "ada@gmail.com", bool trusted = true, string? name = "Ada", string? picture = null) =>
        new("google", subject, email, trusted, name, picture);

    /// <summary>A sign-in of <paramref name="identity"/> that may create an account, with fresh ids.</summary>
    /// <param name="identity">The verified identity.</param>
    private protected static SignInRequest SignIn(VerifiedIdentity identity) =>
        new(identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now + _session, null);

    /// <summary>A sign-in of <paramref name="identity"/> that may create an account and ensures a personal tenant.</summary>
    /// <param name="identity">The verified identity.</param>
    private protected static SignInRequest PersonalSignIn(VerifiedIdentity identity) =>
        SignIn(identity) with { PersonalTenant = new PersonalTenantRequest(new TenantId(Guid.NewGuid())) };

    /// <summary>The prefixed name of the framework table <paramref name="suffix"/> names.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="suffix">An <c>AlvoFrameworkTables</c> suffix.</param>
    protected static string TableOf(IServiceProvider services, string suffix) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix;

    private static async Task<long> CountAsync(IServiceProvider services, string suffix) =>
        Convert.ToInt64(await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, suffix)}"), CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="sql"/> on its own connection and returns the first column of the first row.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="sql">The statement.</param>
    protected static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
