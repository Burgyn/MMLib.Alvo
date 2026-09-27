using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Host.Internal;
using System.Net;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>The set-password page and the endpoint it posts to, over the shipped composition.</b>
/// </summary>
/// <remarks>
/// <para>
/// Every fact runs through <see cref="AlvoHostWorld"/>, because what is under test is the arrangement: the page the
/// dashboard renders, the endpoint the host maps beside it, the antiforgery pair between them, the shared credential
/// limiter in front of both posts, and the redemption the identity package owns. Each piece has its own suite; this
/// one is about whether the host joins them the way design §2 says.
/// </para>
/// <para>
/// The browser's part (the fragment read, the address bar cleared) is the end-to-end suite's. Here a form is posted
/// the way a browser posts it: the antiforgery cookie from the page's response, and the token from its markup.
/// </para>
/// </remarks>
public sealed partial class SetPasswordEndpointTests
{
    private const string Descriptor = "host-user-admin.alvo.json";
    private const string Eva = "eva@alvo.test";
    private const string Otto = "otto@alvo.test";
    private const string NewPassword = "correct horse battery staple";
    private const string Failed = $"{AlvoAdmin.SetPasswordPath}?failed=true";
    private const string BootstrapEmail = "bootstrap-admin@alvo.test";
    private const string BootstrapPassword = "the bootstrap passphrase";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A good post sets the password, answers 303 to sign-in with the notice, and the password signs in.</summary>
    [Fact]
    public async Task A_good_post_sets_the_password_and_sends_the_person_to_sign_in()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));

        using var response = await new Browser(world).SetPasswordAsync(Eva, token, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        LocationOf(response).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        using var signedIn = await new Browser(world).SignInAsync(Eva, NewPassword);
        LocationOf(signedIn).ShouldBe(AlvoAdmin.BasePath, "the password the page set is the one sign-in accepts");
    }

    /// <summary><b>A post without an antiforgery token changes nothing</b>, and the token is still redeemable.</summary>
    [Fact]
    public async Task A_post_without_an_antiforgery_token_changes_nothing()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));

        using var forged = await new Browser(world).PostAsync(AlvoAdmin.SetPasswordEndpoint, Form(Eva, token, NewPassword));

        forged.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        LocationOf(forged).ShouldBe(AlvoAdmin.SetPasswordPath);
        using var genuine = await new Browser(world).SetPasswordAsync(Eva, token, NewPassword);
        LocationOf(genuine).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true", "the forged post consumed nothing");
    }

    /// <summary>
    /// <b>The login-CSRF analogue is refused</b>: a cross-site form carries the attacker's own antiforgery token, and
    /// the victim's browser sends the victim's antiforgery cookie with it; the pair does not match.
    /// </summary>
    [Fact]
    public async Task A_form_carrying_another_browsers_antiforgery_token_is_refused()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));
        var attacker = new Browser(world);
        var victim = new Browser(world);
        var attackersToken = await attacker.AntiforgeryTokenAsync(AlvoAdmin.SetPasswordPath);
        _ = await victim.AntiforgeryTokenAsync(AlvoAdmin.SetPasswordPath);

        using var crossSite = await victim.PostAsync(
            AlvoAdmin.SetPasswordEndpoint, Form(Eva, token, NewPassword, antiforgery: attackersToken));

        LocationOf(crossSite).ShouldBe(AlvoAdmin.SetPasswordPath);
        using var genuine = await victim.SetPasswordAsync(Eva, token, NewPassword);
        LocationOf(genuine).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
    }

    /// <summary>
    /// <b>Every refusal is the same bytes</b>: an unknown address, a wrong token, a disabled person, the bootstrap
    /// administrator and an expired token all answer one <c>Location</c>, and it carries no fragment.
    /// </summary>
    [Fact]
    public async Task Every_refusal_answers_one_location_with_no_fragment()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, Bootstrap(WriteSecret()));
        var eva = await CreateAsync(world, Eva);
        var otto = await CreateAsync(world, Otto);
        var evasToken = await IssueAsync(world, eva);
        var ottosToken = await IssueAsync(world, otto);
        await AdministerAsync(world, people => people.SetDisabledAsync(otto, disabled: true, Ct));
        var bootstrapToken = await IssueAsync(world, await IdOfAsync(world, BootstrapEmail));

        string[] locations =
        [
            await RefusalAsync(world, "nobody@alvo.test", evasToken),
            await RefusalAsync(world, Eva, evasToken[..^12]),
            await RefusalAsync(world, Otto, ottosToken),
            await RefusalAsync(world, BootstrapEmail, bootstrapToken),
            await ExpiredRefusalAsync(),
        ];

        locations.ShouldAllBe(location => location == Failed);
        using var bootstrap = await new Browser(world).SignInAsync(BootstrapEmail, BootstrapPassword);
        LocationOf(bootstrap).ShouldBe(AlvoAdmin.BasePath, "the bootstrap administrator's file password still signs in");
    }

    /// <summary>Two different passwords, and a weak one, come back to the page with the link's fragment carried back.</summary>
    [Fact]
    public async Task A_mismatch_and_a_weak_password_return_to_the_page_with_the_fragment()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));
        var fragment = SetPasswordLink.Fragment(Eva, token);

        using var mismatch = await new Browser(world).SetPasswordAsync(Eva, token, NewPassword, repeat: NewPassword + "!");
        using var weak = await new Browser(world).SetPasswordAsync(Eva, token, "too short");
        using var huge = await new Browser(world).SetPasswordAsync(Eva, token, new string('q', 129));

        LocationOf(mismatch).ShouldBe($"{AlvoAdmin.SetPasswordPath}?problem=mismatch{fragment}");
        LocationOf(weak).ShouldBe($"{AlvoAdmin.SetPasswordPath}?problem=weak{fragment}");
        LocationOf(huge).ShouldBe($"{AlvoAdmin.SetPasswordPath}?problem=weak{fragment}");
        using var genuine = await new Browser(world).SetPasswordAsync(Eva, token, NewPassword);
        LocationOf(genuine).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true", "neither consumed the token");
    }

    /// <summary>A token that went through <c>URLSearchParams</c> (every <c>+</c> read as a space) is repaired.</summary>
    [Fact]
    public async Task A_token_whose_plus_signs_became_spaces_still_redeems()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueShapedAsync(world, await CreateAsync(world, Eva), TokenShape.PlusInside);

        using var response = await new Browser(world).SetPasswordAsync(Eva, $"  {token.Replace('+', ' ')} ", NewPassword);

        LocationOf(response).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
    }

    /// <summary>
    /// <b>A token that ends in <c>+</c> survives the same trip</b>, with pasted whitespace after it: the space that was
    /// its last <c>+</c> is not trimmed away with the whitespace, because base64 comes in quartets of characters.
    /// </summary>
    [Fact]
    public async Task A_token_whose_last_plus_became_a_space_still_redeems_after_pasted_whitespace()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueShapedAsync(world, await CreateAsync(world, Eva), TokenShape.PlusAtTheEnd);

        using var response = await new Browser(world).SetPasswordAsync(Eva, $"  {token.Replace('+', ' ')}  \n", NewPassword);

        LocationOf(response).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
    }

    /// <summary>A token shape a fact needs, found among issued tokens rather than assumed of one.</summary>
    private enum TokenShape
    {
        /// <summary>At least one <c>+</c>, and not as the last character.</summary>
        PlusInside,

        /// <summary>A <c>+</c> as the last character of an unpadded quartet.</summary>
        PlusAtTheEnd,
    }

    /// <summary>
    /// <b>Over-long input is refused before the store is touched</b>: an address over 256 characters or a token over
    /// 2048 is a refusal like any other.
    /// </summary>
    [Fact]
    public async Task Input_over_its_bound_is_a_refusal()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));

        (await RefusalAsync(world, new string('e', 250) + "@alvo.test", token)).ShouldBe(Failed);
        (await RefusalAsync(world, Eva, token + new string('A', 2048))).ShouldBe(Failed);
    }

    /// <summary>
    /// <b>With the per-subject limit at 3, the fourth post for one token is throttled</b>: a 303 back to the page, with
    /// Retry-After. Another token from the same client is still answered.
    /// </summary>
    [Fact]
    public async Task The_fourth_post_for_one_token_is_throttled_and_another_token_is_not()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, Limits(attempts: 3));
        var browser = new Browser(world);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var allowed = await browser.SetPasswordAsync("nobody@alvo.test", "not-a-token", NewPassword);
            LocationOf(allowed).ShouldBe(Failed);
        }

        using var throttled = await browser.SetPasswordAsync("nobody@alvo.test", "not-a-token", NewPassword);
        using var other = await browser.SetPasswordAsync("nobody@alvo.test", "another-token", NewPassword);

        throttled.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        LocationOf(throttled).ShouldBe($"{AlvoAdmin.SetPasswordPath}?throttled=true");
        throttled.Headers.RetryAfter.ShouldNotBeNull();
        throttled.Headers.CacheControl?.NoStore.ShouldBeTrue();
        LocationOf(other).ShouldBe(Failed, "one token's flood throttles that token, not the client");
    }

    /// <summary>
    /// <b>A flood for one address does not throttle another address from the same client</b>, so nobody behind a
    /// shared address can lock a colleague out of sign-in. The throttled answer keeps where they were going.
    /// </summary>
    [Fact]
    public async Task A_sign_in_flood_for_one_address_does_not_throttle_another_from_the_same_client()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, Limits(attempts: 3));
        var browser = new Browser(world);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await browser.SignInAsync("Mallory@alvo.test", "a wrong password here", "/admin/access")).Dispose();
        }

        using var flooded = await browser.SignInAsync("mallory@ALVO.test", "a wrong password here", "/admin/access");
        using var colleague = await browser.SignInAsync(Eva, "a wrong password here");

        LocationOf(flooded).ShouldBe($"{AlvoAdmin.SignInPath}?throttled=true&returnUrl=%2Fadmin%2Faccess");
        LocationOf(colleague).ShouldStartWith($"{AlvoAdmin.SignInPath}?failed=true");
    }

    /// <summary>
    /// <b>A post that fails antiforgery consumes nothing</b>: a cross-site page in a victim's browser cannot spend
    /// the victim's budget, per subject or per client.
    /// </summary>
    [Fact]
    public async Task A_post_failing_antiforgery_consumes_no_budget()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, Limits(attempts: 1, ceiling: 1));
        var token = await IssueAsync(world, await CreateAsync(world, Eva));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var forged = await new Browser(world).PostAsync(AlvoAdmin.SetPasswordEndpoint, Form(Eva, token, NewPassword));
            LocationOf(forged).ShouldBe(AlvoAdmin.SetPasswordPath);
        }

        using var genuine = await new Browser(world).SetPasswordAsync(Eva, token, NewPassword);
        LocationOf(genuine).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
    }

    /// <summary>
    /// <b>The per-client ceiling still throttles</b> a client that cycles addresses and tokens, across both forms.
    /// </summary>
    [Fact]
    public async Task The_ceiling_throttles_a_client_across_subjects_and_both_forms()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, Limits(ceiling: 3));
        var browser = new Browser(world);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await browser.SignInAsync($"guess-{attempt}@alvo.test", "a wrong password here");
            LocationOf(failed).ShouldStartWith($"{AlvoAdmin.SignInPath}?failed=true");
        }

        using var setPassword = await browser.SetPasswordAsync("nobody@alvo.test", "not-a-token", NewPassword);
        using var signIn = await browser.SignInAsync("yet-another@alvo.test", "a wrong password here");

        LocationOf(setPassword).ShouldBe($"{AlvoAdmin.SetPasswordPath}?throttled=true");
        LocationOf(signIn).ShouldStartWith($"{AlvoAdmin.SignInPath}?throttled=true");
    }

    /// <summary>A count that is not a positive number is refused at start, naming the variable to fix.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="limit">The configured value.</param>
    [Theory]
    [InlineData("Alvo:Admin:CredentialAttemptsPerMinute", "0")]
    [InlineData("Alvo:Admin:CredentialAttemptsPerMinute", "-5")]
    [InlineData("Alvo:Admin:CredentialCeilingPerMinute", "0")]
    [InlineData("Alvo:Admin:CredentialCeilingPerMinute", "many")]
    public async Task A_limit_that_is_not_positive_is_refused_at_start(string key, string limit)
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(Descriptor, new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = limit }));

        refusal.Message.ShouldContain(key.Replace(":", "__", StringComparison.Ordinal));
    }

    /// <summary>With the dashboard off there is no credential post to limit, so its counts are not validated.</summary>
    [Fact]
    public async Task With_the_dashboard_off_a_bad_limit_does_not_fail_the_start()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{AlvoAdmin.ConfigurationSection}:Enabled"] = "false",
            ["Alvo:Admin:CredentialAttemptsPerMinute"] = "0",
        });

        using var page = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoAdmin.SetPasswordPath);
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// <b>A body over 16 KB is refused before anything reads it</b>, on both credential posts, and consumes nothing.
    /// </summary>
    [Fact]
    public async Task A_body_over_sixteen_kilobytes_is_refused_before_it_is_read()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));
        var browser = new Browser(world);
        var padded = Form(Eva, token, NewPassword, antiforgery: await browser.AntiforgeryTokenAsync(AlvoAdmin.SetPasswordPath));
        padded["padding"] = new string('p', 17 * 1024);

        using var setPassword = await browser.PostAsync(AlvoAdmin.SetPasswordEndpoint, padded);
        using var signIn = await browser.PostAsync(AlvoAdmin.SignInEndpoint, padded);

        LocationOf(setPassword).ShouldBe(AlvoAdmin.SetPasswordPath);
        LocationOf(signIn).ShouldBe(AlvoAdmin.SignInPath);
        using var genuine = await browser.SetPasswordAsync(Eva, token, NewPassword);
        LocationOf(genuine).ShouldBe($"{AlvoAdmin.SignInPath}?passwordSet=true");
    }

    /// <summary>
    /// A client is its address: an IPv6 one by its /64, which a single host is routinely handed whole, and an
    /// IPv4-mapped one as the IPv4 address, so a dual-stack client has one budget.
    /// </summary>
    /// <param name="first">One address.</param>
    /// <param name="second">Another.</param>
    /// <param name="same">Whether they are one client.</param>
    [Theory]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("::ffff:10.42.0.7", "10.42.0.7", true)]
    [InlineData("10.42.0.7", "10.42.0.8", false)]
    public void A_client_is_its_ipv4_address_or_its_ipv6_slash_64(string first, string second, bool same)
        => (AlvoAdminCredentialLimit.ClientOf(IPAddress.Parse(first)) == AlvoAdminCredentialLimit.ClientOf(IPAddress.Parse(second)))
            .ShouldBe(same);

    /// <summary>
    /// <b>The ceiling bounds the limiter's memory</b>: once it refuses a client, that client's new subjects create no
    /// partition, however many it cycles.
    /// </summary>
    [Fact]
    public void Once_the_ceiling_refuses_a_client_its_new_subjects_create_no_partition()
    {
        using var limit = new AlvoAdminCredentialLimit(Options.Create(
            new AlvoAdminCredentialLimitOptions { AttemptsPerMinute = 20, CeilingPerMinute = 3 }));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.42.0.7");

        var throttled = Enumerable.Range(0, 50)
            .Count(attempt => limit.Charge(http, AlvoAdminCredentialLimit.SignInSubject($"GUESS-{attempt}@ALVO.TEST")) is not null);

        throttled.ShouldBe(47);
        limit.SubjectPartitionsCreated.ShouldBe(3);
    }

    /// <summary>
    /// A sign-in's subject is the address as Identity finds it: every spelling of one account, Unicode normalisation
    /// forms included, is one budget, and the key is a fixed-length hash holding nothing typed.
    /// </summary>
    [Fact]
    public void A_sign_in_subject_is_one_per_account_and_holds_nothing_typed()
    {
        var normalizer = new UpperInvariantLookupNormalizer();
        string Subject(string email) => AlvoAdminCredentialLimit.SignInSubject(normalizer.NormalizeEmail(email));

        Subject("jos\u00e9@alvo.test").ShouldBe(Subject("jose\u0301@ALVO.test"));
        Subject(new string('e', 250) + "@alvo.test").Length.ShouldBe(Subject("a@alvo.test").Length);
        Subject("mallory@alvo.test").ShouldNotContain("MALLORY");
    }

    /// <summary>An address over 256 characters is a failed sign-in, answered before it is keyed or looked up.</summary>
    [Fact]
    public async Task An_address_over_its_bound_is_a_failed_sign_in()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        using var response = await new Browser(world).SignInAsync(new string('e', 300) + "@alvo.test", "a wrong password here");

        LocationOf(response).ShouldStartWith($"{AlvoAdmin.SignInPath}?failed=true");
    }

    /// <summary>
    /// <b>A chunked body cut off past 16 KB is refused like a declared one</b>, on all three anonymous posts, never
    /// an exception page. TestServer enforces no body limit, so the fact installs Kestrel's behaviour: the lowered
    /// maximum wraps the body in a stream that throws Kestrel's own 413 exception past it.
    /// </summary>
    /// <param name="endpoint">The post.</param>
    /// <param name="page">Where a refusal is sent.</param>
    [Theory]
    [InlineData(AlvoAdmin.SetPasswordEndpoint, AlvoAdmin.SetPasswordPath)]
    [InlineData(AlvoAdmin.SignInEndpoint, AlvoAdmin.SignInPath)]
    [InlineData(AlvoAdmin.SignOutEndpoint, AlvoAdmin.SignInPath)]
    public async Task A_chunked_body_cut_off_past_sixteen_kilobytes_is_refused_with_the_page(string endpoint, string page)
    {
        var limit = new KestrelBodyLimit();
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, configure: builder =>
            builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(limit));
        var browser = new Browser(world);
        var form = Form(Eva, "not-a-token", NewPassword, antiforgery: await browser.AntiforgeryTokenAsync(page));
        form["padding"] = new string('p', 17 * 1024);
        var body = new FormUrlEncodedContent(form);
        var chunked = new StreamContent(new UnseekableStream(await body.ReadAsStreamAsync(Ct)));
        chunked.Headers.ContentType = body.Headers.ContentType;

        using var response = await browser.PostAsync(endpoint, chunked);

        ((int)response.StatusCode).ShouldBeInRange(300, 399, "a refusal is the page, never an exception page");
        LocationOf(response).ShouldBe(page);
        limit.CutOffs.ShouldBe(1, "the body went chunked and was cut off, not refused by its declared length");
    }

    /// <summary>The limiter's keys never hold a credential: a token is keyed by a hash of its first characters.</summary>
    [Fact]
    public void A_set_password_subject_holds_no_part_of_the_token()
    {
        var token = "CfDJ8" + new string('x', 200);

        AlvoAdminCredentialLimit.SetPasswordSubject(token).ShouldNotContain("xxxx");
        AlvoAdminCredentialLimit.SetPasswordSubject(token).ShouldBe(AlvoAdminCredentialLimit.SetPasswordSubject(token[..^20]));
    }

    /// <summary>With the dashboard turned off, the page and the endpoint are both gone.</summary>
    [Fact]
    public async Task With_the_dashboard_off_the_page_and_the_endpoint_are_404()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{AlvoAdmin.ConfigurationSection}:Enabled"] = "false",
        });

        using var page = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoAdmin.SetPasswordPath);
        using var post = await world.SendAnonymouslyAsync(HttpMethod.Post, AlvoAdmin.SetPasswordEndpoint);

        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        post.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The page is served with <c>Cache-Control: no-store</c> and <c>Referrer-Policy: no-referrer</c>, and its form is
    /// the one design §1 describes: a real post to the endpoint, the policy on the box, the autocomplete hints.
    /// </summary>
    [Fact]
    public async Task The_page_is_not_stored_sends_no_referrer_and_posts_a_real_form()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoAdmin.SetPasswordPath);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        html.ShouldContain($"action=\"{AlvoAdmin.SetPasswordEndpoint}\"");
        html.ShouldContain("data-enhance=\"false\"");
        html.ShouldContain("autocomplete=\"username\"");
        CountOf(html, "autocomplete=\"new-password\"").ShouldBe(2);
        html.ShouldContain($"minlength=\"{SetPasswordPolicy.MinimumLength}\"");
        html.ShouldContain($"maxlength=\"{SetPasswordPolicy.MaximumLength}\"");
        html.ShouldContain("spellcheck=\"false\"");
    }

    /// <summary>Each state the endpoint redirects to renders its sentence, and none says which half was wrong.</summary>
    /// <param name="query">The query the endpoint redirects with.</param>
    /// <param name="sentence">What the page says.</param>
    [Theory]
    [InlineData("failed=true", "This link does not work. It may have been used already, have expired, or been copied incompletely. Ask your administrator for a new one.")]
    [InlineData("problem=mismatch", "The two passwords are not the same.")]
    [InlineData("problem=weak", "Choose a password of at least 15 characters that is not your email address.")]
    [InlineData("throttled=true", "Too many attempts from this network. Wait a minute, then open your link again.")]
    public async Task Each_state_renders_its_sentence(string query, string sentence)
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, $"{AlvoAdmin.SetPasswordPath}?{query}");

        TextOf(await response.Content.ReadAsStringAsync(Ct)).ShouldContain(sentence);
    }

    /// <summary>The sign-in page says the password is set, and says when the shared limit throttled it.</summary>
    /// <param name="query">The query a redirect lands on sign-in with.</param>
    /// <param name="sentence">What the page says.</param>
    [Theory]
    [InlineData("passwordSet=true", "Your password is set. Sign in with it.")]
    [InlineData("throttled=true", "Too many attempts from this network. Wait a minute and try again.")]
    public async Task The_sign_in_page_renders_the_notice_it_is_sent_back_with(string query, string sentence)
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, $"{AlvoAdmin.SignInPath}?{query}");

        TextOf(await response.Content.ReadAsStringAsync(Ct)).ShouldContain(sentence);
    }

    /// <summary><b>No log record holds the token, the password or the address</b>, whatever the outcome.</summary>
    [Fact]
    public async Task No_log_record_holds_the_token_the_password_or_the_address()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        var token = await IssueAsync(world, await CreateAsync(world, Eva));

        (await new Browser(world).SetPasswordAsync(Eva, token, NewPassword, repeat: "different entirely")).Dispose();
        (await new Browser(world).SetPasswordAsync(Eva, token, "too short")).Dispose();
        (await new Browser(world).SetPasswordAsync(Eva, token[..^12], NewPassword)).Dispose();
        (await new Browser(world).SetPasswordAsync(Eva, token, NewPassword)).Dispose();

        var logged = string.Join('\n', world.Logs.Entries.Select(entry => $"{entry.Message} {entry.Exception}"));
        logged.ShouldNotContain(token[..24]);
        logged.ShouldNotContain(Uri.EscapeDataString(token)[..24]);
        logged.ShouldNotContain(NewPassword);
        logged.ShouldNotContain(Eva);
        logged.ShouldContain("A password was set with a credential token", Case.Sensitive, "the success is recorded");
    }

    /// <summary>
    /// The OpenAPI document describes neither post: both are <c>ExcludeFromDescription</c>, and the TeaPie suite pins
    /// the document's path set by equality.
    /// </summary>
    [Fact]
    public async Task The_openapi_document_describes_neither_credential_post()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.OpenApiDocumentPath);
        var document = await response.ReadJsonObjectAsync();

        document["paths"]!.AsObject().Select(path => path.Key)
            .Where(path => path.StartsWith(AlvoAdmin.BasePath, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>
    /// The dashboard's own <c>15</c> (the box's <c>minlength</c>, the sentence under it) is the identity package's
    /// <c>RequiredLength</c>: the dashboard cannot reference the package, so this is where the two are one number.
    /// </summary>
    [Fact]
    public async Task The_dashboards_minimum_length_is_the_identity_policys()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        world.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password.RequiredLength
            .ShouldBe(SetPasswordPolicy.MinimumLength);
    }

    /// <summary>
    /// <b>The ceiling of 128 is one number</b> (final branch review, item 6): the page's <c>maxlength</c>, the
    /// endpoint's cut-off before any store read, and the identity policy the host registered. The policy's is read
    /// by what it does — it runs before the account is looked at, so for an address with no account a password at
    /// the ceiling gets past it (<c>Refused</c>) and one character more does not (<c>PasswordRejected</c>) — because
    /// the identity package's constant is its own internal, and its behaviour is what a drift would change.
    /// </summary>
    [Fact]
    public async Task The_dashboards_and_the_endpoints_ceiling_is_the_identity_policys()
    {
        AlvoAdminSetPassword.MaximumPasswordLength.ShouldBe(SetPasswordPolicy.MaximumLength);

        await using var world = await AlvoHostWorld.StartAsync(Descriptor);
        using var scope = world.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<Identity.AlvoSignIn>();
        Task<Identity.AlvoPasswordSetOutcome> Redeem(int length) => signIn.SetPasswordAsync(
            "nobody@example.test", "t", new string('q', length), TestContext.Current.CancellationToken);

        (await Redeem(SetPasswordPolicy.MaximumLength))
            .ShouldBe(Identity.AlvoPasswordSetOutcome.Refused, "a password at the ceiling passes the policy");
        (await Redeem(SetPasswordPolicy.MaximumLength + 1))
            .ShouldBe(Identity.AlvoPasswordSetOutcome.PasswordRejected, "one character over it does not");
    }

    /// <summary>
    /// The standalone host maps the set-password post, so the dashboard hands a token over as the link to the page, not
    /// as the bare token an embedded host without one gets (final branch review, item 15).
    /// </summary>
    [Fact]
    public async Task The_standalone_host_has_a_set_password_page_for_the_dashboard_to_link_to()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor);

        world.Services.GetRequiredService<SetPasswordRoute>().IsMapped.ShouldBeTrue();
    }

    private static async Task<string> RefusalAsync(AlvoHostWorld world, string email, string token)
    {
        using var response = await new Browser(world).SetPasswordAsync(email, token, NewPassword);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>An expired token's refusal, from a host of its own whose tokens live one millisecond.</summary>
    private static async Task<string> ExpiredRefusalAsync()
    {
        await using var world = await AlvoHostWorld.StartAsync(Descriptor, configure: builder =>
            builder.Services.Configure<DataProtectionTokenProviderOptions>(
                tokens => tokens.TokenLifespan = TimeSpan.FromMilliseconds(1)));
        var token = await IssueAsync(world, await CreateAsync(world, Eva));
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        return await RefusalAsync(world, Eva, token);
    }

    private static string LocationOf(HttpResponseMessage response) => response.Headers.Location!.OriginalString;

    /// <summary>A page's text as a reader reads it: the tags gone, and every run of whitespace one space.</summary>
    private static string TextOf(string html)
        => WebUtility.HtmlDecode(Whitespace().Replace(Tag().Replace(html, " "), " "));

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static int CountOf(string text, string needle) => Regex.Count(text, Regex.Escape(needle));

    private static Dictionary<string, string?> Limits(int? attempts = null, int? ceiling = null) => new(StringComparer.Ordinal)
    {
        ["Alvo:Admin:CredentialAttemptsPerMinute"] = attempts?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Alvo:Admin:CredentialCeilingPerMinute"] = ceiling?.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Dictionary<string, string?> Bootstrap(string secret) => new(StringComparer.Ordinal)
    {
        ["Alvo:Admin:BootstrapEmail"] = BootstrapEmail,
        ["Alvo:Admin:BootstrapPasswordFile"] = secret,
    };

    private static string WriteSecret()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-set-password-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, BootstrapPassword);
        return path;
    }

    private static Dictionary<string, string> Form(
        string email, string token, string password, string? repeat = null, string? antiforgery = null)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["email"] = email,
            ["token"] = token,
            ["password"] = password,
            ["repeat"] = repeat ?? password,
        };
        if (antiforgery is not null)
        {
            form["__RequestVerificationToken"] = antiforgery;
        }

        return form;
    }

    private static async Task<UserId> CreateAsync(AlvoHostWorld world, string email)
    {
        UserId id = default;
        await AdministerAsync(world, async people =>
            id = (await people.CreateAsync(new AlvoUserCreation(email, []), Ct)).Id);
        return id;
    }

    private static async Task<UserId> IdOfAsync(AlvoHostWorld world, string email)
    {
        using var scope = world.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        return (await users.FindByEmailAsync(email, Ct)).ShouldNotBeNull().Id;
    }

    private static async Task<string> IssueAsync(AlvoHostWorld world, UserId user)
    {
        using var scope = world.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey).IssueCredentialTokenAsync(user, Ct)).Token;
    }

    /// <summary>
    /// Issues tokens until one has the <paramref name="shape"/> a fact is about, so the fact is never vacuous: an
    /// issued token is random, and one that ends in <c>+</c> comes about once in seventy.
    /// </summary>
    /// <remarks>2048 tries leave a chance of about one in 10<sup>13</sup> of finding none.</remarks>
    private static async Task<string> IssueShapedAsync(AlvoHostWorld world, UserId user, TokenShape shape)
    {
        for (var attempt = 0; attempt < 2048; attempt++)
        {
            var token = await IssueAsync(world, user);
            if (Has(token, shape))
            {
                return token;
            }
        }

        throw new InvalidOperationException($"2048 tokens and not one of shape {shape}: the token is no longer base64");
    }

    private static bool Has(string token, TokenShape shape) => shape switch
    {
        TokenShape.PlusInside => token.Contains('+', StringComparison.Ordinal) && !token.EndsWith('+'),
        TokenShape.PlusAtTheEnd => token.EndsWith('+') && token.Length % 4 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static async Task AdministerAsync(AlvoHostWorld world, Func<IAlvoUserAdministration, Task> write)
    {
        using var scope = world.Services.CreateScope();
        await write(scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey));
    }

    /// <summary>
    /// One browser: a cookie jar over the world's client, and the two forms posted the way a browser posts them,
    /// with the antiforgery cookie from the page's response and the token from its markup.
    /// </summary>
    /// <param name="world">The host.</param>
    private sealed partial class Browser(AlvoHostWorld world)
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        /// <summary>Opens the page and fills in the set-password form.</summary>
        public async Task<HttpResponseMessage> SetPasswordAsync(
            string email, string token, string password, string? repeat = null)
        {
            var antiforgery = await AntiforgeryTokenAsync(AlvoAdmin.SetPasswordPath);
            return await PostAsync(AlvoAdmin.SetPasswordEndpoint, Form(email, token, password, repeat, antiforgery));
        }

        /// <summary>Opens the sign-in page and signs in.</summary>
        public async Task<HttpResponseMessage> SignInAsync(string email, string password, string returnUrl = "")
        {
            var antiforgery = await AntiforgeryTokenAsync(AlvoAdmin.SignInPath);
            return await PostAsync(AlvoAdmin.SignInEndpoint, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["email"] = email,
                ["password"] = password,
                ["returnUrl"] = returnUrl,
                ["__RequestVerificationToken"] = antiforgery,
            });
        }

        /// <summary>The antiforgery token the page at <paramref name="path"/> renders, keeping its cookie.</summary>
        public async Task<string> AntiforgeryTokenAsync(string path)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path));
            var html = await response.Content.ReadAsStringAsync(Ct);
            var token = WebUtility.HtmlDecode(AntiforgeryInput().Match(html).Groups["value"].Value);
            token.ShouldNotBeNullOrEmpty($"{path} rendered no antiforgery token");
            return token;
        }

        /// <summary>Posts <paramref name="form"/> with this browser's cookies.</summary>
        public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
            => PostAsync(path, new FormUrlEncodedContent(form));

        /// <summary>Posts <paramref name="content"/> with this browser's cookies.</summary>
        public Task<HttpResponseMessage> PostAsync(string path, HttpContent content)
            => SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = content });

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using (request)
            {
                if (_cookies.Count > 0)
                {
                    request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                }

                var response = await world.Client.SendAsync(request, Ct);
                Keep(response);
                return response;
            }
        }

        private void Keep(HttpResponseMessage response)
        {
            foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = header.Split(';', 2)[0].Split('=', 2);
                _cookies[pair[0]] = pair[1];
            }
        }

        [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"")]
        private static partial Regex AntiforgeryInput();
    }

    /// <summary>Kestrel's body limit, which TestServer does not have: lowering the maximum cuts the body off past it.</summary>
    private sealed class KestrelBodyLimit : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private int _cutOffs;

        /// <summary>How many bodies were cut off past their maximum.</summary>
        public int CutOffs => Volatile.Read(ref _cutOffs);

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, proceed) =>
                {
                    context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(new Limit(context, this));
                    await proceed(context);
                });
                next(app);
            };

        private sealed class Limit(HttpContext context, KestrelBodyLimit owner) : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
        {
            private long? _maximum;

            public bool IsReadOnly => false;

            public long? MaxRequestBodySize
            {
                get => _maximum;
                set
                {
                    _maximum = value;
                    context.Request.Body = new CutOff(context.Request.Body, value ?? long.MaxValue, owner);
                }
            }
        }

        private sealed class CutOff(Stream inner, long maximum, KestrelBodyLimit owner) : Stream
        {
            private long _read;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => Counted(await inner.ReadAsync(buffer, cancellationToken));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private int Counted(int read)
            {
                _read += read;
                if (_read <= maximum)
                {
                    return read;
                }

                Interlocked.Increment(ref owner._cutOffs);
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }
        }
    }

    /// <summary>A stream with no length, so the client sends it chunked.</summary>
    private sealed class UnseekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
