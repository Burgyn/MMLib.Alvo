using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Identity.Internal;
using System.Net;
using System.Security.Claims;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// <b>A session outlives its account only until the next thing it asks for.</b>
/// </summary>
/// <remarks>
/// <para>
/// The cookie is an eight-hour sliding ticket, and without re-checking it a disabled operator kept
/// passing <c>[Authorize]</c> for as long as they kept clicking — the data behind each screen was
/// refused, but the shell kept navigating. Two checks close it, one per transport: the cookie is
/// re-validated against the store on every request, and a Blazor circuit's authentication state is
/// re-validated on an interval, because a circuit issues no requests to validate.
/// </para>
/// <para>
/// <b>A real pipeline over <see cref="TestServer"/>, with the package's own registration.</b> The
/// facts are about what <c>AddAlvoIdentityCookieSignIn</c> composes, so the pipeline is the smallest
/// one that runs it: the cookie scheme, one sign-in endpoint over <see cref="AlvoSignIn"/> and one
/// endpoint that requires authorization. The sign-in takes its credentials from the query string so
/// the fact is about the cookie, not about antiforgery on a form.
/// </para>
/// </remarks>
public sealed class AlvoIdentitySessionTests : IAsyncLifetime
{
    private const string Password = "Str0ng!Passw0rd";
    private const string Email = "eva@example.test";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private readonly StoreProbe _probe = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));
        builder.Services.AddAlvoIdentityCookieSignIn("/sign-in");
        builder.Services.Replace(ServiceDescriptor.Scoped<IAlvoUserStore>(provider => new ProbedStore(
            ActivatorUtilities.CreateInstance<AlvoIdentityUserStore>(provider), _probe)));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapPost("/sign-in", async (string email, string password, AlvoSignIn signIn)
            => await signIn.PasswordSignInAsync(email, password) ? Results.Ok() : Results.Unauthorized());
        _app.MapGet("/me", () => Results.Ok()).RequireAuthorization();
        _app.MapGet("/asset.css", () => Results.Ok()).WithMetadata(Asset("asset.css"));
        _app.MapGet("/sign-in", () => Results.Ok()).AllowAnonymous();
        _app.MapGet("/open", () => Results.Ok());
        _app.MapGet("/requirement", () => Results.Ok()).WithMetadata(new RequiresAuthentication());
        _app.MapPost("/sign-in-without-stamp", async (string id, HttpContext http) => await http.SignInAsync(
            IdentityConstants.ApplicationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"))))
            .AllowAnonymous();

        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        /* This file's pool only: clearing every pool in the process races the other classes' open connections. */
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
    }

    /// <summary>
    /// The cookie of an operator disabled after signing in is refused on the very next request, and
    /// cleared, rather than honoured for the rest of its eight hours.
    /// </summary>
    [Fact]
    public async Task A_cookie_whose_operator_is_disabled_afterwards_is_refused_on_the_next_request()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        using var refused = await GetMeAsync(cookie);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect, "a disabled operator's cookie is a challenge, not a pass");
        refused.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith("http://localhost/sign-in");
        refused.Headers.GetValues("Set-Cookie").ShouldContain(
            header => header.StartsWith("alvo.session=;", StringComparison.Ordinal),
            "the refused cookie is cleared, so the browser stops presenting it");
    }

    /// <summary>
    /// <b>Failed sign-ins against an operator's address do not end their open session.</b> They do
    /// lock the address out of signing in again, which is Identity's own protection and stays.
    /// </summary>
    [Fact]
    public async Task Failed_sign_ins_against_an_operators_address_lock_the_sign_in_but_not_the_open_session()
    {
        await CreateAsync();
        var cookie = await SignInAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await SignInAsync("wrong-password", expected: HttpStatusCode.Unauthorized)).ShouldBeNull();
        }

        (await GetMeAsync(cookie)).StatusCode.ShouldBe(
            HttpStatusCode.OK, "five wrong passwords typed by somebody else must not revoke this session");
        (await SignInAsync(Password, expected: HttpStatusCode.Unauthorized)).ShouldBeNull(
            "the lockout still refuses the next sign-in, even with the right password");
    }

    /// <summary>
    /// The dashboard's circuit sees the same decision on its interval: the registered authentication
    /// state provider is a revalidating one, and it drops a disabled operator.
    /// </summary>
    [Fact]
    public async Task The_circuits_authentication_state_is_revalidated_against_the_store()
    {
        var id = await CreateAsync();
        using var scope = _app.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>()
            .ShouldBeOfType<AlvoIdentityRevalidatingAuthenticationStateProvider>();
        provider.ShouldBeAssignableTo<RevalidatingServerAuthenticationStateProvider>();

        var session = Session(id.ToString(), await StampAsync(id));
        (await provider.StillStandsAsync(session, Ct)).ShouldBeTrue();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await provider.StillStandsAsync(session, Ct)).ShouldBeFalse(
            "a disabled operator's open circuit drops to sign-in on the next revalidation");
    }

    /// <summary>
    /// Every refusal the decision has, default-deny: no subject, a subject that is not a user id, the
    /// reserved all-zero id, and an id the store does not hold.
    /// </summary>
    /// <param name="subject">The subject claim, or <see langword="null"/> for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0199f7a2-6c1e-7000-8000-000000000001")]
    public async Task A_session_that_names_nobody_the_store_holds_does_not_stand(string? subject)
    {
        using var scope = _app.Services.CreateScope();
        var provider = (AlvoIdentityRevalidatingAuthenticationStateProvider)
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();

        (await provider.StillStandsAsync(Session(subject), Ct)).ShouldBeFalse();
    }

    /// <summary>A temporary lockout keeps an open circuit, for the reason the cookie fact gives.</summary>
    [Fact]
    public async Task A_temporarily_locked_out_operators_circuit_still_stands()
    {
        var id = await CreateAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync("wrong-password", expected: HttpStatusCode.Unauthorized);
        }

        using var scope = _app.Services.CreateScope();
        var provider = (AlvoIdentityRevalidatingAuthenticationStateProvider)
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();

        (await provider.StillStandsAsync(Session(id.ToString(), await StampAsync(id)), Ct)).ShouldBeTrue();
    }

    /// <summary>
    /// <b>A password set elsewhere ends a session minted before it</b>: the cookie is refused and cleared on its
    /// next guarded request — baas-analyza §2.2's "password change → old sessions dead".
    /// </summary>
    [Fact]
    public async Task A_cookie_minted_before_a_password_is_set_is_refused_and_cleared_on_its_next_request()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetPasswordAsync(id, "an entirely new passphrase");

        using var refused = await GetMeAsync(cookie);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refused.Headers.GetValues("Set-Cookie").ShouldContain(
            header => header.StartsWith("alvo.session=;", StringComparison.Ordinal));
    }

    /// <summary>An open circuit's state from before a password was set drops on its next revalidation.</summary>
    [Fact]
    public async Task A_circuit_state_from_before_a_password_is_set_does_not_stand()
    {
        var id = await CreateAsync();
        var before = Session(id.ToString(), await StampAsync(id));
        using var scope = _app.Services.CreateScope();
        var provider = (AlvoIdentityRevalidatingAuthenticationStateProvider)
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        (await provider.StillStandsAsync(before, Ct)).ShouldBeTrue();

        await SetPasswordAsync(id, "an entirely new passphrase");

        (await provider.StillStandsAsync(before, Ct)).ShouldBeFalse();
    }

    /// <summary>
    /// <b>A cookie carrying no security stamp is refused</b> (default-deny), even for an enabled account: every
    /// cookie this package mints carries one, so a cookie without it was not minted by a sign-in.
    /// </summary>
    [Fact]
    public async Task A_cookie_without_a_security_stamp_is_refused()
    {
        var id = await CreateAsync();
        using var response = await _client.PostAsync($"/sign-in-without-stamp?id={id}", null, Ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Select(header => header.Split(';')[0])
            .Single(header => header.StartsWith("alvo.session=", StringComparison.Ordinal));

        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    /// <summary>A circuit whose principal carries no stamp, or a stamp that is not the stored one, does not stand.</summary>
    /// <param name="stamp">The stamp claim, or <see langword="null"/> for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT-THE-STORED-STAMP")]
    public async Task A_circuit_without_the_stored_stamp_does_not_stand(string? stamp)
    {
        var id = await CreateAsync();
        using var scope = _app.Services.CreateScope();
        var provider = (AlvoIdentityRevalidatingAuthenticationStateProvider)
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();

        (await provider.StillStandsAsync(Session(id.ToString(), stamp), Ct)).ShouldBeFalse();
    }

    /// <summary>
    /// A role or tenant change does <b>not</b> end the session, on either transport: neither rotates the stamp, so the
    /// stamp check ends sessions on a password change and a disable, and on nothing else an administrator does.
    /// </summary>
    [Fact]
    public async Task A_role_or_tenant_change_does_not_end_the_session()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        var circuit = Session(id.ToString(), await StampAsync(id));

        await AdministerAsync(people => people.SetRolesAsync(id, ["developer"], Ct));
        await AdministerAsync(people => people.SetTenantAsync(id, TenantId.New(), Ct));

        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CircuitStandsAsync(circuit)).ShouldBeTrue();
    }

    /// <summary>
    /// <b>Letting a person back in does not revive a session from before the disable.</b> The disable rotates the
    /// stamp, so a cookie that was not presented while they were disabled — a stolen one, say — stays dead after the
    /// re-enable, and they sign in again (OWASP: end sessions when an account is disabled).
    /// </summary>
    [Fact]
    public async Task A_cookie_from_before_a_disable_stays_refused_after_the_person_is_let_back_in()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));
        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: false, Ct));

        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await SignInAsync()).ShouldNotBeNull("a person let back in signs in again");
    }

    /// <summary>The circuit's half: an open tab from before a disable does not stand again after the re-enable.</summary>
    [Fact]
    public async Task A_circuit_from_before_a_disable_does_not_stand_after_the_person_is_let_back_in()
    {
        var id = await CreateAsync();
        var before = Session(id.ToString(), await StampAsync(id));

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));
        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: false, Ct));

        (await CircuitStandsAsync(before)).ShouldBeFalse();
        (await CircuitStandsAsync(Session(id.ToString(), await StampAsync(id)))).ShouldBeTrue();
    }

    /// <summary>Asks the registered circuit provider, from a scope of its own, whether a state still stands.</summary>
    /// <param name="state">The circuit's authentication state.</param>
    /// <returns>Whether it stands.</returns>
    private async Task<bool> CircuitStandsAsync(AuthenticationState state)
    {
        using var scope = _app.Services.CreateScope();
        var provider = (AlvoIdentityRevalidatingAuthenticationStateProvider)
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        return await provider.StillStandsAsync(state, Ct);
    }

    /// <summary>
    /// <b>Parallel cookie checks during identity writes are answered, not failed.</b> A first page load
    /// sends every stylesheet, script and font at once, each with the cookie; an administration write in
    /// flight on the same SQLite file must make them wait, never answer <c>500</c>.
    /// </summary>
    [Fact]
    public async Task Parallel_cookie_checks_during_identity_writes_all_succeed()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        var bystander = await CreateOtherAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var writes = Task.Run(async () =>
        {
            var count = 0;
            while (!stop.IsCancellationRequested)
            {
                await AdministerAsync(people => people.SetTenantAsync(bystander, TenantId.New(), Ct));
                count++;
            }

            return count;
        }, Ct);

        var statuses = new List<HttpStatusCode>();
        for (var wave = 0; wave < 10; wave++)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
            {
                using var response = await GetMeAsync(cookie);
                return response.StatusCode;
            }));
            statuses.AddRange(answers);
        }

        await stop.CancelAsync();
        (await writes).ShouldBeGreaterThan(0, "the checks must really have raced writes");
        statuses.ShouldAllBe(status => status == HttpStatusCode.OK);
        id.ShouldNotBe(default);
    }

    /// <summary>
    /// <b>The cookie is re-checked only where it guards something.</b> A request to an endpoint with no
    /// authorization requirement — a stylesheet, a font, the sign-in page — never reads the store, so a first
    /// page load is not a burst of store reads and a store blip cannot turn the sign-in page into a 500.
    /// </summary>
    [Fact]
    public async Task A_request_to_an_endpoint_that_guards_nothing_does_not_read_the_store()
    {
        await CreateAsync();
        var cookie = await SignInAsync();
        var before = _probe.Reads;

        (await GetAsync("/asset.css", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/sign-in", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Reads.ShouldBe(before, "a stylesheet or the sign-in page has nothing for a stale cookie to reach");

        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Reads.ShouldBe(before + 1, "a protected endpoint is re-checked on every request");
    }

    /// <summary>
    /// A store outage fails a protected request closed, and leaves the requests that guard nothing — the
    /// sign-in page among them — answering.
    /// </summary>
    [Fact]
    public async Task A_store_outage_fails_a_protected_request_closed_and_leaves_the_sign_in_page_up()
    {
        await CreateAsync();
        var cookie = await SignInAsync();
        _probe.Down = true;

        (await GetAsync("/sign-in", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/asset.css", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Should.ThrowAsync<InvalidOperationException>(async () => await GetMeAsync(cookie),
            "could not tell whether the account is enabled is never a pass on a protected endpoint");
    }

    /// <summary>A disabled operator's cookie on an unprotected endpoint is not refused — and not honoured anywhere it matters.</summary>
    [Fact]
    public async Task A_disabled_operators_cookie_is_ignored_on_an_asset_and_refused_on_a_protected_route()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await GetAsync("/asset.css", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetMeAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    /// <summary>
    /// <b>An endpoint with no authorization metadata is still checked.</b> It can authorize imperatively — an
    /// <c>AuthorizeView</c>, a handler calling <c>IAuthorizationService</c> or <c>User.IsInRole</c> — and would
    /// otherwise be handed a disabled operator's principal, role claims and all.
    /// </summary>
    [Fact]
    public async Task An_endpoint_with_no_authorization_metadata_is_still_checked()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        var before = _probe.Reads;

        (await GetAsync("/open", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Reads.ShouldBe(before + 1);

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));
        using var refused = await GetAsync("/open", cookie);
        refused.Headers.GetValues("Set-Cookie").ShouldContain(
            header => header.StartsWith("alvo.session=;", StringComparison.Ordinal),
            "the disabled operator's cookie is rejected and cleared even where nothing requires it");
    }

    /// <summary>
    /// An endpoint whose requirement is <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationRequirementData"/>
    /// — the third kind of metadata the authorization middleware reads — refuses a disabled operator's cookie.
    /// </summary>
    [Fact]
    public async Task An_endpoint_guarded_by_requirement_data_refuses_a_disabled_operators_cookie()
    {
        var id = await CreateAsync();
        var cookie = await SignInAsync();
        (await GetAsync("/requirement", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await GetAsync("/requirement", cookie)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    /// <summary>
    /// <b>The skip is an allow-list:</b> only a static asset and an explicitly anonymous endpoint go
    /// unchecked. Everything else — no metadata, any kind of requirement, a hub (even one marked anonymous,
    /// because its connection becomes a circuit's authentication state), a request nothing matched — is checked.
    /// </summary>
    [Fact]
    public void Only_a_static_asset_or_an_explicitly_anonymous_endpoint_skips_the_check()
    {
        static Endpoint With(params object[] metadata) => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "e");
        var anonymous = new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute();
        var hub = new Microsoft.AspNetCore.SignalR.HubMetadata(typeof(Microsoft.AspNetCore.SignalR.Hub));

        AlvoSessionValidation.Guards(With(Asset("app.css"))).ShouldBeFalse();
        AlvoSessionValidation.Guards(With(anonymous)).ShouldBeFalse();
        AlvoSessionValidation.Guards(With(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute(), anonymous)).ShouldBeFalse();

        AlvoSessionValidation.Guards(null).ShouldBeTrue("an unmatched request cannot be shown to guard nothing");
        AlvoSessionValidation.Guards(With()).ShouldBeTrue("no metadata is not proof of guarding nothing");
        AlvoSessionValidation.Guards(With(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute())).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(new RequiresAuthentication())).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(hub)).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(hub, anonymous)).ShouldBeTrue();
    }

    /// <summary>The metadata <c>MapStaticAssets</c> puts on each asset's endpoint.</summary>
    /// <param name="route">The asset's route.</param>
    /// <returns>A descriptor for it.</returns>
    private static Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor Asset(string route) => new()
    {
        Route = route,
        AssetPath = route,
        Selectors = [],
        Properties = [],
        ResponseHeaders = [],
    };

    /// <summary>An authenticated principal with <paramref name="subject"/> as its user id claim.</summary>
    /// <param name="subject">The subject, or <see langword="null"/> for a principal with none.</param>
    /// <param name="stamp">The security stamp claim, or <see langword="null"/> for a principal with none.</param>
    /// <returns>The authentication state a circuit would hold.</returns>
    private static AuthenticationState Session(string? subject, string? stamp = null)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        if (stamp is not null)
        {
            claims.Add(new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, stamp));
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    /// <summary>The stamp the store holds for <paramref name="id"/> now — what a sign-in would put in the cookie.</summary>
    /// <param name="id">The operator.</param>
    /// <returns>The stored stamp.</returns>
    private async Task<string> StampAsync(UserId id)
    {
        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        return await users.GetSecurityStampAsync((await users.FindByIdAsync(id.ToString())).ShouldNotBeNull());
    }

    /// <summary>Issues a credential token and redeems it, as the set-password page does, from scopes of their own.</summary>
    /// <param name="id">Whose password to set.</param>
    /// <param name="password">The new password.</param>
    /// <returns>A task that completes when the password is set.</returns>
    private async Task SetPasswordAsync(UserId id, string password)
    {
        AlvoCredentialToken token = null!;
        await AdministerAsync(async people => token = await people.IssueCredentialTokenAsync(id, Ct));

        using var scope = _app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AlvoSignIn>().SetPasswordAsync(Email, token.Token, password))
            .ShouldBe(AlvoPasswordSetOutcome.Set);
    }

    /// <summary>Creates the operator with a password, from a scope of its own.</summary>
    /// <returns>The operator's identifier.</returns>
    private async Task<UserId> CreateAsync()
    {
        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var row = new AlvoIdentityUser { Id = Guid.CreateVersion7(), UserName = Email, Email = Email };
        (await users.CreateAsync(row, Password)).Succeeded.ShouldBeTrue();
        return new UserId(row.Id);
    }

    /// <summary>Creates a second person, whom the concurrent writes are aimed at.</summary>
    /// <returns>Their identifier.</returns>
    private async Task<UserId> CreateOtherAsync()
    {
        UserId other = default;
        await AdministerAsync(async people =>
            other = (await people.CreateAsync(new AlvoUserCreation("otto@example.test", []), Ct)).Id);
        return other;
    }

    /// <summary>Signs in through the endpoint and returns the session cookie it set.</summary>
    /// <param name="password">The password to present.</param>
    /// <param name="expected">The status the attempt is expected to answer.</param>
    /// <returns>The <c>name=value</c> of the session cookie, or <see langword="null"/> when none was set.</returns>
    private async Task<string?> SignInAsync(string password = Password, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await _client.PostAsync(
            $"/sign-in?email={Uri.EscapeDataString(Email)}&password={Uri.EscapeDataString(password)}", null, Ct);
        response.StatusCode.ShouldBe(expected);

        return response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(cookie => cookie.Split(';')[0]).FirstOrDefault(
                cookie => cookie.StartsWith("alvo.session=", StringComparison.Ordinal))
            : null;
    }

    /// <summary>Asks for the protected endpoint, presenting <paramref name="cookie"/>.</summary>
    /// <param name="cookie">The session cookie.</param>
    /// <returns>The response.</returns>
    private Task<HttpResponseMessage> GetMeAsync(string? cookie) => GetAsync("/me", cookie);

    /// <summary>Asks for <paramref name="path"/>, presenting <paramref name="cookie"/>.</summary>
    /// <param name="path">The endpoint.</param>
    /// <param name="cookie">The session cookie.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie.ShouldNotBeNull());
        return await _client.SendAsync(request, Ct);
    }

    /// <summary>Runs one administration call in a fresh scope — a second administrator's request.</summary>
    /// <param name="write">The call to make.</param>
    /// <returns>A task that completes when the scope has been disposed.</returns>
    private async Task AdministerAsync(Func<IAlvoUserAdministration, Task> write)
    {
        using var other = _app.Services.CreateScope();
        await write(other.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey));
    }

    /// <summary>What the probed store was asked, and whether it is down.</summary>
    private sealed class StoreProbe
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public bool Down { get; set; }

        public void Read()
        {
            Interlocked.Increment(ref _reads);
            if (Down)
            {
                throw new InvalidOperationException("The identity store is unreachable.");
            }
        }
    }

    /// <summary>The real store, counting the id lookups the cookie check makes.</summary>
    private sealed class ProbedStore(IAlvoUserStore inner, StoreProbe probe) : IAlvoUserStore
    {
        public ValueTask<AlvoUser?> FindAsync(UserId user, CancellationToken cancellationToken)
        {
            probe.Read();
            return inner.FindAsync(user, cancellationToken);
        }

        public ValueTask<AlvoUser?> FindByEmailAsync(string email, CancellationToken cancellationToken)
            => inner.FindByEmailAsync(email, cancellationToken);

        public ValueTask<IReadOnlyList<AlvoUser>> ListAsync(CancellationToken cancellationToken)
            => inner.ListAsync(cancellationToken);

        public ValueTask SetRolesAsync(UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken)
            => inner.SetRolesAsync(user, roleNames, cancellationToken);
    }

    /// <summary>A requirement carried as <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationRequirementData"/>, with no <c>[Authorize]</c> beside it.</summary>
    private sealed class RequiresAuthentication : Microsoft.AspNetCore.Authorization.IAuthorizationRequirementData
    {
        public IEnumerable<Microsoft.AspNetCore.Authorization.IAuthorizationRequirement> GetRequirements()
            => [new Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement()];
    }
}
