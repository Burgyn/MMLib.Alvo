using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using MMLib.Alvo.Host;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A real Alvo, with a real dashboard, on a real port, in a real browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The host is the shipped one.</b> <see cref="AlvoHost.CreateBuilder"/> and
/// <see cref="AlvoHost.BuildAsync"/> are what the container runs, so this suite exercises the
/// composition a deployment gets rather than an approximation assembled for testing. The only
/// things it supplies are the four an operator supplies: a descriptor, a database, and the
/// bootstrap administrator's address and password file.
/// </para>
/// <para>
/// <b>A real Kestrel port rather than a <c>TestServer</c>.</b> A browser cannot talk to an
/// in-memory transport, and half of what this suite exists to check — that the page becomes
/// interactive, that the design system loads, that a form posts — is only true over HTTP.
/// </para>
/// <para>
/// <b>One host and one database per class, discarded afterwards.</b> The scenarios apply
/// descriptors and write rows, so sharing one across classes would make them order-dependent —
/// which is the failure mode where a suite goes green in the wrong order and red in CI.
/// </para>
/// </remarks>
public sealed class AdminWorld : IAsyncLifetime
{
    /// <summary>
    /// How long any one scenario may take before it is a failure rather than a wait.
    /// </summary>
    /// <remarks>
    /// <b>Every scenario carries this, and the reason is the failure mode of a browser suite.</b>
    /// A locator that waits for something which never appears does not fail fast — it waits, and a
    /// handful of those turn a job that should take a minute into one that looks hung and is
    /// eventually killed by the runner with no message at all. A timeout converts that into a
    /// named scenario and a stack, which is the difference between a bug report and a mystery.
    /// Generous on purpose: a cold browser and a first circuit are genuinely slow.
    /// </remarks>
    public const int ScenarioTimeout = 150_000;

    /// <summary>The address the bootstrap administrator signs in with.</summary>
    public const string AdminEmail = "admin@alvo.test";

    /// <summary>Their password. A test secret, in a file, exactly as a deployment supplies one.</summary>
    public const string AdminPassword = "Alvo-e2e-Bootstrap-1!";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"alvo-admin-e2e-{Guid.CreateVersion7():N}");

    private WebApplication? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>Where the dashboard is.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>The browser every scenario opens a context in.</summary>
    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("The world has not started yet.");

    /// <summary>
    /// The running host's own services, for a scenario that has to change the world behind the
    /// browser's back.
    /// </summary>
    /// <remarks>
    /// Used to reach the identity store directly — the dashboard cannot disable the only
    /// administrator it has, and a state that only arrives from outside the screens is one the
    /// screens still have to answer for.
    /// </remarks>
    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("The world has not started yet.");

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var descriptor = Path.Combine(_root, "descriptor.json");
        var password = Path.Combine(_root, "bootstrap.pwd");
        await File.WriteAllTextAsync(descriptor, Descriptors.FieldService).ConfigureAwait(false);
        await File.WriteAllTextAsync(password, AdminPassword).ConfigureAwait(false);

        var port = FreePort();
        BaseAddress = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";

        /* --environment on the command line, not builder.Environment afterwards.
           WebApplication.CreateBuilder decides whether to load static web assets while it is
           building — from the environment it can see at that moment — so setting the name on the
           finished builder is too late: the decision has been made, the static-web-asset file
           provider is absent, and every asset is then served as an empty 200. Which is worse than a
           404: the page renders, unstyled and inert, and every assertion but the ones about
           interactivity still passes. */
        var builder = AlvoHost.CreateBuilder(
            ["--environment", "Development"],
            configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Alvo:DescriptorPath"] = descriptor,
                ["Alvo:Database:Provider"] = "Sqlite",
                ["Alvo:Database:SqliteConnectionString"] = $"Data Source={Path.Combine(_root, "alvo.db")}",
                ["Alvo:Admin:BootstrapEmail"] = AdminEmail,
                ["Alvo:Admin:BootstrapPasswordFile"] = password,

                /* Quiet, and this is not cosmetic. The host runs INSIDE the test process, so every
                   log line it writes goes through the test platform's own output sink — and at the
                   default level that is one line per EF command, which a screen like Rules produces
                   dozens of per render. The sink becomes the bottleneck and the suite looks like it
                   is hanging while it is really writing SQL to a buffer. A warning that matters
                   still comes through. */
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            }));

        /* Through configuration rather than UseUrls: WebApplicationBuilder's web-host shim is
           deliberately narrow, and "urls" is the key it reads anyway. */
        builder.Configuration["urls"] = BaseAddress;

        _app = await AlvoHost.BuildAsync(builder).ConfigureAwait(false);
        await _app.StartAsync().ConfigureAwait(false);

        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            /* A SQLite handle the provider has not released yet. The directory is under the
               system's temp root and the operating system reclaims it; failing a green suite over
               a leftover file would be the wrong trade. */
        }
    }

    /// <summary>
    /// Opens a page, signed in as the bootstrap administrator, with the console asserted clean.
    /// </summary>
    /// <remarks>
    /// <b>The console is an assertion, not a log.</b> A caught exception that blanks a screen
    /// leaves a rendered shell and a silent error, which is exactly the failure a screenshot review
    /// misses. Every scenario that uses this gets that check for free.
    /// </remarks>
    /// <param name="cancel">
    /// The scenario's own token. Playwright takes no cancellation token — every call carries its
    /// own timeout instead — so this is observed between steps: a scenario that has run out of
    /// time stops at the next one rather than at the end of the last wait.
    /// </param>
    /// <param name="width">The viewport width — 1400 for a desktop, 375 for the phone criterion.</param>
    /// <returns>The session.</returns>
    public async Task<AdminSession> SignInAsync(CancellationToken cancel, int width = 1400)
    {
        cancel.ThrowIfCancellationRequested();

        var context = await Browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = width, Height = width < 720 ? 780 : 950 },
        }).ConfigureAwait(false);

        /* A minute, for the reason the sign-in wait below carries: a server-interactive circuit's
           first render pays for the browser, the connection and the component tree at once. */
        context.SetDefaultTimeout(60_000);

        var session = new AdminSession(
            context, await context.NewPageAsync().ConfigureAwait(false), BaseAddress);
        session.Watch();

        await session.Page.GotoAsync($"{BaseAddress}{AlvoAdmin.SignInPath}").ConfigureAwait(false);
        await session.Page.FillAsync("#email", AdminEmail).ConfigureAwait(false);
        await session.Page.FillAsync("#password", AdminPassword).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        await session.Page.ClickAsync("button[type=submit]").ConfigureAwait(false);

        try
        {
            /* A minute rather than Playwright's thirty seconds: the first sign-in of a run pays for a
               cold browser process and for the first render of a server-interactive circuit at
               once, and a suite that fails one run in ten for that reason teaches everyone to
               re-run it instead of reading it. */
            await session.Page.WaitForURLAsync(
                $"**{AlvoAdmin.BasePath}", new() { Timeout = 60_000 }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            /* A sign-in that does not land is the one failure worth explaining rather than timing
               out on: the message names where it stopped and what the screen said, which is the
               difference between "the credentials are wrong" and "the endpoint threw". */
            var said = await session.Page.Locator(".a-error__detail").AllInnerTextsAsync()
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Signing in did not reach the dashboard. At {session.Page.Url}. "
                + $"The screen said: {string.Join(" | ", said)}");
        }
        await session.SettleAsync().ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// A port nothing is listening on.
    /// </summary>
    /// <remarks>
    /// Bound and released rather than picked at random: a random number collides on a machine
    /// running several of these at once, and a suite that fails one run in twenty for that reason
    /// teaches everyone to re-run it.
    /// </remarks>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
