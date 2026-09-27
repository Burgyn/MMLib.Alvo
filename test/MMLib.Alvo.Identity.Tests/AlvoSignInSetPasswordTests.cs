using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// <b>A credential token is redeemed once, by the person it was issued to, and by nobody else.</b>
/// </summary>
/// <remarks>
/// <para>
/// Real SQLite, real <see cref="UserManager{TUser}"/> and the package's own registration: every fact is about
/// the order <see cref="AlvoSignIn.SetPasswordAsync"/> runs Identity's checks in, and a fake store would prove
/// only that the fake agrees. The hasher is the real one behind a counting decorator, because "no failure path
/// hashes a password" is the deterministic stand-in for timing parity — a wall-clock assertion would be flaky.
/// </para>
/// </remarks>
public sealed class AlvoSignInSetPasswordTests : IAsyncLifetime
{
    private const string Eva = "eva@example.test";
    private const string Otto = "otto@example.test";
    private const string OldPassword = "Str0ng!Passw0rd";
    private const string NewPassword = "correct horse battery staple";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-set-password-{Guid.NewGuid():N}.db");
    private readonly HashCounter _hashes = new();
    private ServiceProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _provider = Build(services => { });
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>().Database.EnsureCreatedAsync(Ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        /* This file's pool only: clearing every pool in the process races the other classes' open connections. */
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
    }

    /// <summary>A valid token sets the password, the new one signs in, and a temporary lockout is cleared.</summary>
    [Fact]
    public async Task A_valid_token_sets_the_password_and_clears_the_lockout()
    {
        var eva = await CreateAsync(Eva, OldPassword);
        await LockOutAsync(Eva);
        var token = await IssueAsync(eva);

        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set);

        var row = await RowAsync(Eva);
        row.AccessFailedCount.ShouldBe(0);
        row.LockoutEnd.ShouldBeNull("the lockout protected the old password; the holder just proved a token");
        (await SignsInAsync(Eva, NewPassword)).ShouldBeTrue();
        (await SignsInAsync(Eva, OldPassword)).ShouldBeFalse();
    }

    /// <summary>A person the dashboard created with no password gets one.</summary>
    [Fact]
    public async Task A_person_created_with_no_password_can_set_one()
    {
        var eva = await CreateAsync(Eva, password: null);

        (await RedeemAsync(Eva, await IssueAsync(eva), NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set);
        (await SignsInAsync(Eva, NewPassword)).ShouldBeTrue();
    }

    /// <summary><b>A token works once.</b> Redeeming it rotates the stamp it carries.</summary>
    [Fact]
    public async Task A_reused_token_is_refused()
    {
        var token = await IssueAsync(await CreateAsync(Eva, OldPassword));
        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set);

        (await RedeemAsync(Eva, token, "another long passphrase")).ShouldBe(AlvoPasswordSetOutcome.Refused);
        (await SignsInAsync(Eva, NewPassword)).ShouldBeTrue("the refused second use changed nothing");
    }

    /// <summary><b>Every outstanding token dies at the first redemption</b>, not only the one used.</summary>
    [Fact]
    public async Task Redeeming_one_of_two_outstanding_tokens_kills_the_other()
    {
        var eva = await CreateAsync(Eva, OldPassword);
        var first = await IssueAsync(eva);
        var second = await IssueAsync(eva);

        (await RedeemAsync(Eva, first, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set);
        (await RedeemAsync(Eva, second, "another long passphrase")).ShouldBe(AlvoPasswordSetOutcome.Refused);
    }

    /// <summary><b>An expired token is refused</b>, under a host's own shorter lifetime.</summary>
    /// <remarks>
    /// The pinned <c>DataProtectorTokenProvider</c> reads the wall clock, not a <see cref="TimeProvider"/>, so
    /// the lifetime is one millisecond rather than a faked clock — and a host's <c>Configure</c> after
    /// <c>AddAlvoIdentity</c> is proven to win on the way.
    /// </remarks>
    [Fact]
    public async Task An_expired_token_is_refused()
    {
        await using var shortLived = Build(services => services.Configure<DataProtectionTokenProviderOptions>(
            tokens => tokens.TokenLifespan = TimeSpan.FromMilliseconds(1)));
        var eva = await CreateAsync(Eva, OldPassword, shortLived);
        var token = await IssueAsync(eva, shortLived);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);

        (await RedeemAsync(Eva, token, NewPassword, shortLived)).ShouldBe(AlvoPasswordSetOutcome.Refused);
    }

    /// <summary>
    /// <b>A colleague's address with Eva's token is refused</b>, and neither password moves: the user id inside
    /// the token is checked against the row the address found.
    /// </summary>
    [Fact]
    public async Task A_token_presented_with_a_colleagues_address_is_refused_and_moves_nobodys_password()
    {
        var eva = await CreateAsync(Eva, OldPassword);
        await CreateAsync(Otto, OldPassword);

        (await RedeemAsync(Otto, await IssueAsync(eva), NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);

        (await SignsInAsync(Eva, OldPassword)).ShouldBeTrue();
        (await SignsInAsync(Otto, OldPassword)).ShouldBeTrue();
    }

    /// <summary><b>Another person's token is refused</b> for Eva's address.</summary>
    [Fact]
    public async Task Another_persons_token_is_refused()
    {
        await CreateAsync(Eva, OldPassword);
        var otto = await CreateAsync(Otto, OldPassword);

        (await RedeemAsync(Eva, await IssueAsync(otto), NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);
        (await SignsInAsync(Eva, OldPassword)).ShouldBeTrue();
    }

    /// <summary>An address nobody holds is refused, exactly like every other refusal.</summary>
    [Fact]
    public async Task An_unknown_address_is_refused()
    {
        var token = await IssueAsync(await CreateAsync(Eva, OldPassword));

        (await RedeemAsync("nobody@example.test", token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);
    }

    /// <summary><b>A tampered, truncated or garbage token is refused</b>, never thrown.</summary>
    /// <param name="mangle">How the genuine token is damaged.</param>
    [Theory]
    [InlineData("flip")]
    [InlineData("truncate")]
    [InlineData("garbage")]
    [InlineData("empty")]
    public async Task A_damaged_token_is_refused(string mangle)
    {
        var token = await IssueAsync(await CreateAsync(Eva, OldPassword));
        var damaged = mangle switch
        {
            "flip" => token[..20] + (token[20] == 'A' ? 'B' : 'A') + token[21..],
            "truncate" => token[..^12],
            "garbage" => "not a token at all %%%",
            _ => string.Empty,
        };

        (await RedeemAsync(Eva, damaged, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);
        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set, "nothing was consumed");
    }

    /// <summary>
    /// <b>A disabled person is refused, and the token is not consumed</b>: it works once they are let back in.
    /// </summary>
    [Fact]
    public async Task A_disabled_person_is_refused_and_the_token_survives_being_let_back_in()
    {
        var eva = await CreateAsync(Eva, OldPassword);
        var token = await IssueAsync(eva);
        await AdministerAsync(people => people.SetDisabledAsync(eva, disabled: true, Ct));

        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);
        (await RowAsync(Eva)).LockoutEnd.ShouldBe(AlvoIdentityLockout.Disabled, "still disabled");

        await AdministerAsync(people => people.SetDisabledAsync(eva, disabled: false, Ct));
        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set);
    }

    /// <summary>
    /// <b>The bootstrap administrator is refused at redemption</b>, even with a token minted through the keyed
    /// unguarded implementation, and the file's password still signs in.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_token_minted_around_the_guard_is_refused()
    {
        var admin = await CreateAsync("admin@example.test", OldPassword);
        _provider.GetRequiredService<AlvoBootstrapAdmin>().Publish(admin);

        var token = await IssueAsync(admin);

        (await RedeemAsync("admin@example.test", token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Refused);
        (await SignsInAsync("admin@example.test", OldPassword)).ShouldBeTrue();
    }

    /// <summary>
    /// <b>A weak password is rejected before anything about the account is read</b> — for a real address and an
    /// unknown one alike — and the token is still good afterwards.
    /// </summary>
    /// <param name="email">The address presented.</param>
    /// <param name="password">The weak password.</param>
    [Theory]
    [InlineData(Eva, "fourteen-chars")]
    [InlineData(Eva, "my eva@example.test pass")]
    [InlineData(Eva, "EVA-is-my-name-forever")]
    [InlineData("nobody@example.test", "fourteen-chars")]
    public async Task A_weak_password_is_rejected_whoever_the_address_is(string email, string password)
    {
        var token = await IssueAsync(await CreateAsync(Eva, OldPassword));

        (await RedeemAsync(email, token, password)).ShouldBe(AlvoPasswordSetOutcome.PasswordRejected);
        (await RedeemAsync(Eva, token, NewPassword)).ShouldBe(AlvoPasswordSetOutcome.Set, "nothing was consumed");
    }

    /// <summary><b>Two redemptions racing with one token: exactly one wins.</b></summary>
    [Fact]
    public async Task Two_parallel_redemptions_of_one_token_set_the_password_once()
    {
        var token = await IssueAsync(await CreateAsync(Eva, OldPassword));

        var outcomes = await Task.WhenAll(
            Task.Run(() => RedeemAsync(Eva, token, NewPassword), Ct),
            Task.Run(() => RedeemAsync(Eva, token, "another long passphrase"), Ct));

        outcomes.Count(outcome => outcome == AlvoPasswordSetOutcome.Set).ShouldBe(1);
        outcomes.Count(outcome => outcome == AlvoPasswordSetOutcome.Refused).ShouldBe(1);
    }

    /// <summary>
    /// <b>No failure path hashes a password</b>, so a refusal costs the same whichever check refused it.
    /// </summary>
    [Fact]
    public async Task No_failure_path_hashes_a_password()
    {
        var eva = await CreateAsync(Eva, OldPassword);
        var otto = await CreateAsync(Otto, OldPassword);
        var token = await IssueAsync(eva);
        var ottos = await IssueAsync(otto);
        _hashes.Reset();

        await RedeemAsync("nobody@example.test", token, NewPassword);
        await RedeemAsync(Eva, ottos, NewPassword);
        await RedeemAsync(Eva, token[..^12], NewPassword);
        await RedeemAsync(Eva, token, "fourteen-chars");
        await AdministerAsync(people => people.SetDisabledAsync(eva, disabled: true, Ct));
        await RedeemAsync(Eva, token, NewPassword);

        _hashes.Calls.ShouldBe(0);
    }

    /// <summary>
    /// <b>Signing in as an address nobody holds costs one hash</b>, exactly like a wrong password, so the answer's
    /// timing does not say which addresses have accounts.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_costs_one_hash_at_sign_in_like_a_wrong_password()
    {
        await CreateAsync(Eva, OldPassword);
        await CreateAsync(Otto, password: null);

        (await HashesForSignInAsync(Eva, "a wrong password here")).ShouldBe(1);
        (await HashesForSignInAsync("nobody@example.test", "a wrong password here")).ShouldBe(1);
        (await HashesForSignInAsync(Otto, "a wrong password here")).ShouldBe(1, "no password yet is no oracle either");

        var eva = await RowAsync(Eva);
        await AdministerAsync(people => people.SetDisabledAsync(new UserId(eva.Id), disabled: true, Ct));
        (await HashesForSignInAsync(Eva, OldPassword)).ShouldBe(1, "nor is a disabled or locked-out account");
    }

    /// <summary>
    /// The fixed hash the unknown-address path verifies against is a real v3 hash — a malformed one would be
    /// refused before PBKDF2 ran, and cost nothing.
    /// </summary>
    [Fact]
    public void The_timing_parity_hash_is_a_full_cost_v3_hash()
    {
        var hasher = new PasswordHasher<AlvoIdentityUser>();

        hasher.VerifyHashedPassword(new AlvoIdentityUser(), AlvoSignIn.TimingParityHash, AlvoSignIn.TimingParityPassword)
            .ShouldBe(PasswordVerificationResult.Success);
    }

    /// <summary>The token's stated expiry is its issue time plus the configured lifetime, not "about a day".</summary>
    [Fact]
    public async Task The_stated_expiry_is_the_issue_time_plus_the_configured_lifetime()
    {
        var lifetime = TimeSpan.FromHours(7);
        await using var configured = Build(services => services.Configure<DataProtectionTokenProviderOptions>(
            tokens => tokens.TokenLifespan = lifetime));
        var eva = await CreateAsync(Eva, OldPassword, configured);

        var before = DateTimeOffset.UtcNow;
        var issued = await IssueFullAsync(eva, configured);
        var after = DateTimeOffset.UtcNow;

        issued.ExpiresAt.ShouldBeInRange(before + lifetime, after + lifetime);
    }

    /// <summary>The default lifetime is Identity's one day, now stated by the package.</summary>
    [Fact]
    public void The_default_token_lifetime_is_one_day()
    {
        _provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataProtectionTokenProviderOptions>>()
            .Value.TokenLifespan.ShouldBe(TimeSpan.FromDays(1));
    }

    /// <summary>
    /// The policy is NIST SP 800-63B-4's for a single-factor password: 15 to 128 characters, no composition rules.
    /// </summary>
    /// <param name="length">The password's length.</param>
    /// <param name="accepted">Whether the policy accepts it.</param>
    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task The_policy_is_fifteen_to_one_hundred_twenty_eight_characters_with_no_composition(
        int length, bool accepted)
    {
        (await PolicyAcceptsAsync(new string('q', length))).ShouldBe(accepted);
    }

    /// <summary>A short local part is not a blocklist: "ab@…" must not refuse every password with "ab" in it.</summary>
    [Fact]
    public async Task A_local_part_too_short_to_be_a_word_does_not_refuse_passwords_containing_it()
    {
        (await PolicyAcceptsAsync("the lab report is overdue", email: "ab@example.test")).ShouldBeTrue();
        (await PolicyAcceptsAsync("ab@example.test is mine", email: "ab@example.test")).ShouldBeFalse();
    }

    private ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));
        services.AddAlvoIdentityCookieSignIn("/sign-in");
        services.Replace(ServiceDescriptor.Scoped<IPasswordHasher<AlvoIdentityUser>>(
            _ => new CountingHasher(new PasswordHasher<AlvoIdentityUser>(), _hashes)));
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<UserId> CreateAsync(string email, string? password, ServiceProvider? host = null)
    {
        using var scope = (host ?? _provider).CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var row = new AlvoIdentityUser { Id = Guid.CreateVersion7(), UserName = email, Email = email };
        var created = password is null ? await users.CreateAsync(row) : await users.CreateAsync(row, password);
        created.Succeeded.ShouldBeTrue();
        return new UserId(row.Id);
    }

    private async Task<string> IssueAsync(UserId user, ServiceProvider? host = null)
        => (await IssueFullAsync(user, host)).Token;

    private async Task<AlvoCredentialToken> IssueFullAsync(UserId user, ServiceProvider? host = null)
    {
        using var scope = (host ?? _provider).CreateScope();
        return await scope.ServiceProvider
            .GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey)
            .IssueCredentialTokenAsync(user, Ct);
    }

    private async Task<AlvoPasswordSetOutcome> RedeemAsync(
        string email, string token, string password, ServiceProvider? host = null)
    {
        using var scope = (host ?? _provider).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AlvoSignIn>().SetPasswordAsync(email, token, password);
    }

    private async Task AdministerAsync(Func<IAlvoUserAdministration, Task> write)
    {
        using var scope = _provider.CreateScope();
        await write(scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey));
    }

    private async Task<AlvoIdentityUser> RowAsync(string email)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        return (await users.FindByEmailAsync(email)).ShouldNotBeNull();
    }

    /// <summary>Whether the password verifies against the stored hash — the check a sign-in makes.</summary>
    private async Task<bool> SignsInAsync(string email, string password)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        return await users.CheckPasswordAsync((await users.FindByEmailAsync(email)).ShouldNotBeNull(), password);
    }

    /// <summary>Five wrong sign-ins: Identity's temporary lockout, as a stranger typing guesses would cause.</summary>
    private async Task LockOutAsync(string email)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var row = (await users.FindByEmailAsync(email)).ShouldNotBeNull();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await users.AccessFailedAsync(row);
        }

        (await users.IsLockedOutAsync(row)).ShouldBeTrue();
    }

    private async Task<int> HashesForSignInAsync(string email, string password)
    {
        using var scope = _provider.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<AlvoSignIn>();
        _hashes.Reset();
        (await signIn.PasswordSignInAsync(email, password)).ShouldBeFalse();
        return _hashes.Calls;
    }

    private async Task<bool> PolicyAcceptsAsync(string password, string email = Eva)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var person = new AlvoIdentityUser { UserName = email, Email = email };
        foreach (var validator in users.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(users, person, password)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many times the hasher was asked to hash or verify.</summary>
    private sealed class HashCounter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Count() => Interlocked.Increment(ref _calls);

        public void Reset() => Volatile.Write(ref _calls, 0);
    }

    /// <summary>The real hasher, counting every call.</summary>
    private sealed class CountingHasher(IPasswordHasher<AlvoIdentityUser> inner, HashCounter counter)
        : IPasswordHasher<AlvoIdentityUser>
    {
        public string HashPassword(AlvoIdentityUser user, string password)
        {
            counter.Count();
            return inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(
            AlvoIdentityUser user, string hashedPassword, string providedPassword)
        {
            counter.Count();
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
