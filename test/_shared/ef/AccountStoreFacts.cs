using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Secrets;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The account store — accounts, identities, sessions, refresh tokens and keys — the same facts on SQLite and on
/// PostgreSQL (accounts spec §5.1, §5.5, §5.6, D6; S2 plan D2, D8, D12). Every instant and id is the fact's own.
/// </summary>
public abstract class AccountStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _session = TimeSpan.FromDays(180);

    private static readonly string[] _authTables =
        ["alvo_auth_accounts", "alvo_auth_identities", "alvo_auth_sessions", "alvo_auth_refresh_tokens", "alvo_auth_keys"];

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"alvo-account-key-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        (await store.IsSessionLiveAsync(account.Id, request.SessionId, _now, Ct)).ShouldBeTrue();
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
            (await store.IsSessionLiveAsync(outcomes[0].Account!.Value, first.SessionId, _now, Ct)).ShouldBeTrue();
            (await store.IsSessionLiveAsync(outcomes[0].Account!.Value, second.SessionId, _now, Ct)).ShouldBeTrue();
        }

        (await CountAsync(services, AlvoFrameworkTables.AuthAccountsSuffix)).ShouldBe(Iterations);
    }

    [Fact]
    public async Task A_session_is_dead_after_its_expiry_and_for_another_account()
    {
        var (store, _) = await StoreAsync();
        var request = SignIn(Identity("sub-1"));
        var account = (await store.SignInAsync(request, Ct)).Account!.Value;

        (await store.IsSessionLiveAsync(account, request.SessionId, request.SessionExpiresAt.AddTicks(-1), Ct)).ShouldBeTrue();
        (await store.IsSessionLiveAsync(account, request.SessionId, request.SessionExpiresAt, Ct)).ShouldBeFalse();
        (await store.IsSessionLiveAsync(UserId.New(), request.SessionId, _now, Ct)).ShouldBeFalse();
        (await store.IsSessionLiveAsync(account, Guid.NewGuid(), _now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public void The_tables_are_reserved_framework_names() =>
        _authTables.Except(AlvoFrameworkTables.NamesFor("alvo"), StringComparer.Ordinal).ShouldBeEmpty();

    private async Task<(IAccountStore Store, IServiceProvider Services)> StoreAsync(bool withKey = true)
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

    private static SignInRequest SignIn(VerifiedIdentity identity) =>
        new(identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now + _session);

    private static string TableOf(IServiceProvider services, string suffix) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix;

    private static async Task<long> CountAsync(IServiceProvider services, string suffix) =>
        Convert.ToInt64(await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, suffix)}"), CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
