using MMLib.Alvo.Api;
using MMLib.Alvo.Tests.Events;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests.Integration;

/// <summary>
/// One standalone host running as a real child process, against a temp SQLite file and a loopback webhook
/// receiver, endable with <see cref="Process.Kill(bool)"/> — SIGKILL, no <c>StopAsync</c>, no disposal, no flush.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only shape in this repository that exercises the crash path at all</b>, which is why it does
/// not reuse <c>AlvoHostWorld</c>: that runs in-process over <c>TestServer</c> and its stop calls
/// <c>StopAsync</c>. The published host is what runs, so the child is assembled by
/// <c>AlvoHost.RunAsync</c> exactly as the container's entry point assembles it.
/// </para>
/// <para>
/// <b>Every wait is bounded, and none of the bounds is optional.</b> A publish that hangs, a child that never
/// binds its port, a kill that does not land and a delivery that never arrives are four ways this harness could
/// stall a CI job that has twenty minutes for all of ring2, so each has a budget and each failure names what it
/// was waiting for and what the child had printed by then:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="_publishBudget"/> — <c>dotnet publish</c>; the whole child output is reported on failure.</description></item>
/// <item><description>
/// <see cref="_readinessBudget"/> — <c>/health/ready</c> answering 200. Checked against
/// <see cref="Process.HasExited"/> on every poll, so a child that <em>cannot</em> boot — a taken port, a refused
/// descriptor — fails in the second it takes to exit rather than waiting the budget out.
/// </description></item>
/// <item><description><see cref="_exitBudget"/> — the kill landing; a child still alive after it is a failure, never a wait.</description></item>
/// <item><description><c>LoopbackWebhookReceiver</c>'s own delivery budget.</description></item>
/// </list>
/// <para>
/// The child is published once per test class into a temp directory keyed by build configuration, and the
/// publish is a real one rather than <c>--no-build</c>: the container image is built by <c>dotnet publish</c>
/// too, and CI may run a newer analyzer set than a local build (#129), so this is where an analyzer error that
/// only CI sees surfaces inside <c>build-test</c> rather than in the e2e's image build.
/// </para>
/// </remarks>
internal sealed class ChildHostHarness : IAsyncDisposable
{
    /// <summary>Publishes the host if needed, starts one child, and returns once it answers readiness.</summary>
    /// <param name="setup">How this child is configured.</param>
    internal static async Task<ChildHostHarness> StartAsync(ChildHostSetup setup)
    {
        var entryPoint = await _published.Value.ConfigureAwait(false);
        var harness = new ChildHostHarness(entryPoint);
        try
        {
            await harness.StartChildAsync(setup).ConfigureAwait(false);

            return harness;
        }
        catch
        {
            await harness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The exit code a real kill produces on this platform, and the one thing a graceful stop cannot forge.
    /// </summary>
    /// <remarks>
    /// On Unix <see cref="Process.Kill(bool)"/> sends <c>SIGKILL</c> and the shell convention reports a
    /// signalled death as <c>128 + signal</c>, so 9 becomes <b>137</b> — measured in spike Q9. On Windows there
    /// are no signals: .NET terminates the process with <c>TerminateProcess(handle, -1)</c>, so the code is
    /// <b>-1</b>. Either value is unreachable by the host's own two exits — <c>0</c> for a stop and <c>78</c>
    /// (<c>EX_CONFIG</c>) for a refused configuration — which is exactly why the crash facts assert it: without
    /// it, a graceful <c>StopAsync</c> would satisfy every other assertion they make.
    /// </remarks>
    internal static int KilledExitCode => OperatingSystem.IsWindows() ? WindowsTerminateExitCode : UnixSigkillExitCode;

    /// <summary>The receiver every delivery this child makes arrives at.</summary>
    internal LoopbackWebhookReceiver Receiver { get; }

    /// <summary>The exit code the last killed child reported.</summary>
    /// <exception cref="InvalidOperationException">No child has exited yet.</exception>
    internal int ExitCode => ExitedCode ?? throw new InvalidOperationException(
        "No child process has exited yet. Call Kill() or WaitUntilExitedAsync() before reading the exit code, "
        + "or the fact would assert against a process that is still running.");

    /// <summary>Every outbox row in the child's database, read off the file rather than out of the child.</summary>
    internal IReadOnlyList<OutboxRowState> OutboxRows() => SqliteOutboxProbe.Rows(_databasePath);

    /// <summary>Creates one order over HTTP, which is what emits the event this harness is about.</summary>
    /// <param name="reference">The order's unique reference.</param>
    /// <param name="status">The order's status.</param>
    internal async Task CreateOrderAsync(string reference = "ORD-1", string status = "queued")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, OrdersPath));
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, $"{AdminKeyId}.{AdminSecret}");
        request.Content = JsonContent.Create(new JsonObject
        {
            ["reference"] = reference,
            ["status"] = status,
        });

        using var response = await _client.SendAsync(request, Ct).ConfigureAwait(false);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "the write must have been accepted by the child, or there is no committed event to lose: "
            + await response.Content.ReadAsStringAsync(Ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Waits out a window in which a running dispatcher would provably have claimed and delivered.
    /// </summary>
    /// <remarks>
    /// <b>An absence read too early is not an absence.</b> Measured: without this wait, a dispatcher that ignored
    /// <c>Alvo:Events:Enabled</c> entirely still left <c>An_event_committed_before_a_kill_is_delivered_after_a_restart</c>
    /// green, because the probe and the kill both completed inside one 100 ms poll interval — so the fact would
    /// have passed with the publish already done, which is the exact vacuity a crash test invites. The budget is
    /// twenty poll intervals, and <c>A_kill_mid_action_makes_the_action_repeat_after_a_restart</c> is the paired
    /// fact showing a delivery really does arrive well inside it.
    /// </remarks>
    internal static Task WaitOutADeliveryWindowAsync() => Task.Delay(_absenceBudget, Ct);

    /// <summary>Ends the child with <c>SIGKILL</c> and waits, bounded, for it to be gone.</summary>
    /// <exception cref="TimeoutException">The child was still alive after <see cref="_exitBudget"/>.</exception>
    /// <remarks>
    /// Idempotent and safe from the receiver's thread as well as a fact's, because the mid-action kill is raised
    /// from inside the webhook request while the fact is waiting on a delivery.
    /// </remarks>
    internal void Kill()
    {
        lock (_killGate)
        {
            var child = _child ?? throw new InvalidOperationException("No child process is running.");
            if (_exitCode is not null)
            {
                return;
            }

            child.Kill(entireProcessTree: true);
            if (!child.WaitForExit((int)_exitBudget.TotalMilliseconds))
            {
                throw new TimeoutException(
                    $"Process {child.Id} was still running {_exitBudget} after Kill(entireProcessTree: true). "
                    + "The kill did not land, so nothing below would be a fact about a crashed host.");
            }

            child.WaitForExit();
            _exitCode = child.ExitCode;
        }
    }

    /// <summary>Waits, bounded, until the child has exited — including a kill raised by the receiver.</summary>
    /// <exception cref="TimeoutException">The child was still running when the budget ran out.</exception>
    internal async Task WaitUntilExitedAsync()
    {
        var deadline = DateTimeOffset.UtcNow + _exitBudget;
        while (ExitedCode is null)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Waited {_exitBudget} for the child host to exit. The child printed: {ChildOutput}");
            }

            await Task.Delay(_pollDelay, Ct).ConfigureAwait(false);
        }
    }

    /// <summary>Starts a replacement child over the same database, descriptor and receiver.</summary>
    /// <param name="setup">How the replacement is configured.</param>
    /// <remarks>
    /// The previous child must really be gone first: a "restart" that overlapped the process it replaced would
    /// have two dispatchers on one outbox, which is the shape the ordering guarantee explicitly does not cover.
    /// </remarks>
    internal async Task RestartAsync(ChildHostSetup setup)
    {
        await WaitUntilExitedAsync().ConfigureAwait(false);
        Forget();

        await StartChildAsync(setup).ConfigureAwait(false);
    }

    /// <summary>Drops the exited child, so the next start is not mistaken for it.</summary>
    private void Forget()
    {
        lock (_killGate)
        {
            _exitCode = null;
            _child = null;
        }
    }

    /// <summary>Waits, bounded, until every outbox row is retired.</summary>
    /// <exception cref="TimeoutException">Something was still pending when the budget ran out.</exception>
    /// <remarks>
    /// The closing half of a redelivery fact: the dispatcher retires an entry only after every matched action has
    /// run, so a repeat that is never retired would be a repeat that failed again rather than one that succeeded.
    /// </remarks>
    internal async Task WaitUntilRetiredAsync()
    {
        var deadline = DateTimeOffset.UtcNow + _exitBudget;
        while (OutboxRows().Any(row => !row.Dispatched))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Waited {_exitBudget} for every outbox row to be retired; "
                    + $"{OutboxRows().Count(row => !row.Dispatched)} still had dispatched_at unset.");
            }

            await Task.Delay(_pollDelay, Ct).ConfigureAwait(false);
        }
    }

    /// <summary>The <c>id</c> of the CloudEvents envelope a delivery carried.</summary>
    /// <param name="body">The delivery's request body.</param>
    internal static Guid EventIdOf(string body) =>
        Guid.Parse((JsonNode.Parse(body) as JsonObject)!["id"]!.GetValue<string>());

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await KillQuietlyAsync().ConfigureAwait(false);
        _client.Dispose();
        Receiver.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_workingDirectory);
    }

    private ChildHostHarness(string entryPoint)
    {
        _entryPoint = entryPoint;
        _workingDirectory = Path.Combine(Path.GetTempPath(), $"alvo-killed-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingDirectory);
        _databasePath = Path.Combine(_workingDirectory, "alvo.db");
        Receiver = LoopbackWebhookReceiver.Start();
        _descriptorPath = WriteDescriptor(_workingDirectory, Receiver.Url);
        _client = new HttpClient { Timeout = _requestTimeout };
    }

    /// <summary>Writes the suite's descriptor with the receiver's real URL in it.</summary>
    /// <param name="directory">Where the child reads its descriptor from.</param>
    /// <param name="receiverUrl">The URL the webhook endpoint must point at.</param>
    /// <exception cref="InvalidOperationException">The placeholder URL was not found in the template.</exception>
    /// <remarks>
    /// The rewrite is verified rather than assumed. A template whose placeholder was renamed would otherwise
    /// leave the child delivering to the discard port, every fact here would time out waiting for a delivery, and
    /// the message would name the wrong cause.
    /// </remarks>
    private static string WriteDescriptor(string directory, string receiverUrl)
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "descriptors", DescriptorFileName));
        var descriptor = template.Replace(PlaceholderReceiverUrl, receiverUrl, StringComparison.Ordinal);
        if (descriptor.Length == template.Length)
        {
            throw new InvalidOperationException(
                $"'{DescriptorFileName}' no longer contains the placeholder URL '{PlaceholderReceiverUrl}', so the "
                + "child would deliver to the discard port and every crash fact would time out on a delivery that "
                + "was never going to arrive.");
        }

        var path = Path.Combine(directory, DescriptorFileName);
        File.WriteAllText(path, descriptor, Encoding.UTF8);

        return path;
    }

    /// <summary>Starts one child process and waits for it to answer readiness.</summary>
    /// <param name="setup">How this child is configured.</param>
    private async Task StartChildAsync(ChildHostSetup setup)
    {
        Receiver.OnFirstDelivery = setup.KillOnFirstDelivery ? Kill : null;
        _port = FreeLoopbackPort();
        _output.Clear();
        _child = Start(Arguments(), Environment(setup));

        await WaitUntilReadyAsync().ConfigureAwait(false);
    }

    private Process Start(IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        return Started(info);
    }

    private Process Started(ProcessStartInfo info)
    {
        var child = Process.Start(info) ?? throw new InvalidOperationException(
            $"Starting 'dotnet {string.Join(' ', info.ArgumentList)}' produced no process.");

        child.OutputDataReceived += Record;
        child.ErrorDataReceived += Record;
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        return child;
    }

    private void Record(object sender, DataReceivedEventArgs line)
    {
        if (line.Data is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line.Data);
        }
    }

    private IEnumerable<string> Arguments() => [_entryPoint];

    /// <summary>
    /// The child's whole configuration, spelled the way a container spells it — double underscores, environment
    /// variables, nothing on the command line.
    /// </summary>
    /// <param name="setup">How this child is configured.</param>
    /// <remarks>
    /// <para>
    /// Modelled on <c>docker-compose.yml</c>'s own <c>alvo</c> service, so a fact here is a fact about the keys an
    /// operator sets. The event settings are stated rather than defaulted: the claim lease is what recovers an
    /// entry a process died holding, so a fact about that recovery must not depend on the shipped five-minute
    /// default, and the poll interval has to be under the lease or the options validation refuses the pair.
    /// </para>
    /// <para>
    /// The lease is the shortest value that stays above the poll interval and above one boot: a killed child's
    /// claim has to be stale by the time its replacement is ready, and the replacement takes about 1.5 s to
    /// answer readiness (spike Q9).
    /// </para>
    /// </remarks>
    private Dictionary<string, string> Environment(ChildHostSetup setup) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = BaseAddress.ToString(),
            ["Alvo__DescriptorPath"] = _descriptorPath,
            ["Alvo__Database__Provider"] = "sqlite",
            ["Alvo__Database__SqliteConnectionString"] = $"Data Source={_databasePath}",
            ["Alvo__Events__Enabled"] = setup.DispatcherEnabled ? "true" : "false",
            ["Alvo__Events__PollInterval"] = "00:00:00.100",
            ["Alvo__Events__BatchSize"] = "10",
            ["Alvo__Events__MaxAttempts"] = "10",
            ["Alvo__Events__ClaimLease"] = "00:00:01",
            ["Alvo__Auth__DevKeys__0__KeyId"] = AdminKeyId,
            ["Alvo__Auth__DevKeys__0__Secret"] = AdminSecret,
            ["Alvo__Auth__DevKeys__0__User"] = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            ["Alvo__Auth__DevKeys__0__Roles__0"] = "admin",
            ["Alvo__Auth__DevKeys__0__Roles__1"] = "authenticated",
            ["Alvo__Auth__DevKeys__0__Scopes__0"] = "*:read",
            ["Alvo__Auth__DevKeys__0__Scopes__1"] = "*:write",
        };

    /// <summary>Polls readiness until the child answers 200, or fails the moment the child dies.</summary>
    private async Task WaitUntilReadyAsync()
    {
        var child = _child!;
        var deadline = DateTimeOffset.UtcNow + _readinessBudget;
        while (!await ReadyAsync().ConfigureAwait(false))
        {
            if (child.HasExited)
            {
                throw new InvalidOperationException(
                    $"The child host exited with code {child.ExitCode} before it became ready — its port may be "
                    + $"taken, or its configuration refused. It printed: {ChildOutput}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The child host did not answer 200 on {AlvoHealth.ReadinessPath} inside {_readinessBudget}. "
                    + $"It printed: {ChildOutput}");
            }

            await Task.Delay(_pollDelay, Ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadyAsync()
    {
        try
        {
            using var response = await _client
                .GetAsync(new Uri(BaseAddress, AlvoHealth.ReadinessPath), Ct)
                .ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Ends the child if it is still running, and never fails a teardown over it.</summary>
    private async Task KillQuietlyAsync()
    {
        try
        {
            if (_child is not null)
            {
                Kill();
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or TimeoutException)
        {
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Publishes the host once per test run, and reports its whole output when it fails.</summary>
    private static async Task<string> PublishAsync()
    {
        var root = RepositoryRoot.Find();
        var output = Path.Combine(Path.GetTempPath(), $"alvo-published-host-{BuildConfiguration}");
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in PublishArguments(root, output))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["HUSKY"] = "0";

        return await PublishedEntryPointAsync(info, output).ConfigureAwait(false);
    }

    private static IEnumerable<string> PublishArguments(string root, string output) =>
    [
        "publish",
        Path.Combine(root, "src", "MMLib.Alvo.Host", "MMLib.Alvo.Host.csproj"),
        "--configuration",
        BuildConfiguration,
        "--output",
        output,
        "--nologo",
    ];

    private static async Task<string> PublishedEntryPointAsync(ProcessStartInfo info, string output)
    {
        using var publish = Process.Start(info)!;
        var log = publish.StandardOutput.ReadToEndAsync();
        var errors = publish.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(_publishBudget);

        await publish.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Publishing the standalone host failed with exit code {publish.ExitCode}. CI may run a newer "
                + $"analyzer set than a local build (#129), so this is where that shows up first.{NewLine}"
                + $"{await log.ConfigureAwait(false)}{NewLine}{await errors.ConfigureAwait(false)}");
        }

        return Path.Combine(output, "MMLib.Alvo.Host.dll");
    }

    /// <summary>A loopback port nothing is listening on, as of this instant.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        try
        {
            probe.Start();

            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private Uri BaseAddress => new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_port}"));

    /// <summary>The exit code of the child that has already exited, or nothing while one is running.</summary>
    private int? ExitedCode
    {
        get
        {
            lock (_killGate)
            {
                return _exitCode;
            }
        }
    }

    private string ChildOutput
    {
        get
        {
            lock (_output)
            {
                return _output.Length == 0 ? "(nothing)" : NewLine + _output.ToString();
            }
        }
    }

    private const string AdminKeyId = "killed-host-admin";
    private const string AdminSecret = "killed-host-admin-secret";
    private const string ApiKeyHeader = "X-Alvo-Api-Key";
    private const string OrdersPath = "/api/orders";
    private const string DescriptorFileName = "killed-host.alvo.json";
    private const string PlaceholderReceiverUrl = "http://127.0.0.1:9/hooks/orders";
    private const int UnixSigkillExitCode = 137;
    private const int WindowsTerminateExitCode = -1;

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private static readonly TimeSpan _absenceBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _publishBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _readinessBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _exitBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollDelay = TimeSpan.FromMilliseconds(50);

    private static readonly Lazy<Task<string>> _published = new(PublishAsync);

    private readonly string _entryPoint;
    private readonly string _workingDirectory;
    private readonly string _databasePath;
    private readonly string _descriptorPath;
    private readonly HttpClient _client;
    private readonly StringBuilder _output = new();
    private readonly object _killGate = new();

    private Process? _child;
    private int? _exitCode;
    private int _port;

    private static string NewLine => System.Environment.NewLine;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>How one child host is configured.</summary>
/// <remarks>
/// Two switches, because the two crash criteria need two windows: the first needs a host that <em>never</em>
/// drains, so the kill provably lands between the commit and the publish; the second needs one that drains and is
/// killed inside the delivery.
/// </remarks>
internal sealed record ChildHostSetup
{
    /// <summary>Whether this child drains the outbox (<c>Alvo:Events:Enabled</c>).</summary>
    internal bool DispatcherEnabled { get; init; }

    /// <summary>Whether the receiver kills this child from inside its first delivery, before responding.</summary>
    internal bool KillOnFirstDelivery { get; init; }
}
