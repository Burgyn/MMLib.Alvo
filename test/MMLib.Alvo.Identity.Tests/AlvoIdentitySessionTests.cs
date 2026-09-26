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
        _app.MapGet("/asset.css", () => Results.Ok());
        _app.MapGet("/sign-in", () => Results.Ok()).AllowAnonymous();

        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
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

        (await provider.StillStandsAsync(Session(id.ToString()), Ct)).ShouldBeTrue();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await provider.StillStandsAsync(Session(id.ToString()), Ct)).ShouldBeFalse(
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

        (await provider.StillStandsAsync(Session(id.ToString()), Ct)).ShouldBeTrue();
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
    /// Which endpoints are re-checked: anything with an authorization requirement, a SignalR hub (the Blazor
    /// circuit's connection carries the principal into the circuit), and a request no endpoint matched —
    /// default-deny; only an endpoint that demonstrably guards nothing is skipped.
    /// </summary>
    [Fact]
    public void The_check_runs_exactly_where_the_cookie_guards_something()
    {
        static Endpoint With(params object[] metadata) => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "e");

        AlvoSessionValidation.Guards(null, fallback: false).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute()), fallback: false).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(new Microsoft.AspNetCore.SignalR.HubMetadata(typeof(Microsoft.AspNetCore.SignalR.Hub))), fallback: false).ShouldBeTrue();
        AlvoSessionValidation.Guards(With(), fallback: true).ShouldBeTrue("a fallback policy protects an endpoint that declares nothing");
        AlvoSessionValidation.Guards(With(), fallback: false).ShouldBeFalse();
        AlvoSessionValidation.Guards(
            With(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute(), new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()),
            fallback: false).ShouldBeFalse();
    }

    /// <summary>An authenticated principal with <paramref name="subject"/> as its user id claim.</summary>
    /// <param name="subject">The subject, or <see langword="null"/> for a principal with none.</param>
    /// <returns>The authentication state a circuit would hold.</returns>
    private static AuthenticationState Session(string? subject)
    {
        var claims = subject is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, subject) };
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
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
}
