using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Leaving a screen with a tab strip navigates nowhere: MudTabs moves its active index as it removes its panels on
/// disposal, and a screen that followed that index navigated from a page that was going away.
/// </summary>
/// <remarks>
/// <para>
/// The fault is the server's, not the browser's: a prerendered page's disposal threw a <see cref="NavigationException"/>
/// that Kestrel logged, and a disposed circuit logged "Navigation failed". So the world keeps the host's navigation
/// errors, and the facts assert there are none after a prerender, an in-app move away, and a closed tab. Each opens a
/// tab other than the first, because the index the removal moves to is the first one, and a screen already there had
/// nothing to follow.
/// </para>
/// <para>
/// <b>The Rules case reproduces; the Schema cases are guards.</b> Against Rules' old <c>ActivePanelIndexChanged</c>
/// binding the Rules case failed on the <see cref="NavigationException"/> it logged. Against the entity screen's old
/// binding the two Schema cases passed: its disposal did not throw in this suite. It was moved to the same binding for
/// the same library path, and these cases keep it there; they have not been seen to fail.
/// </para>
/// </remarks>
/// <param name="world">A host whose navigation errors are kept.</param>
public sealed class TabDisposalScenarios(NavigationLogWorld world) : IClassFixture<NavigationLogWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/schema/customers?tab=indexes", "**/schema/customers?tab=indexes")]
    [InlineData("/schema/customers?tab=api", "**/schema/customers?tab=api")]
    [InlineData("/rules/work_orders", "**/rules/work_orders")]
    public async Task Leaving_a_tabbed_screen_navigates_nowhere(string route, string address)
    {
        var earlier = world.NavigationErrors.Count;
        var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using (session)
        {
            await session.GoAsync(route);
            await session.Page.WaitForURLAsync(address);
            await session.Page.GetByRole(AriaRole.Tab, new() { Selected = true }).WaitForAsync();

            await session.Page.GetByTestId("sidebar")
                .GetByRole(AriaRole.Link, new() { Name = "Configuration history", Exact = true }).ClickAsync();
            await session.Page.WaitForURLAsync("**/admin/history");
            await session.Page.GetByRole(AriaRole.Heading, new() { Name = "Configuration history" }).WaitForAsync();
            await session.Page.WaitForTimeoutAsync(500);

            session.Page.Url.ShouldEndWith("/admin/history", Case.Sensitive, "nothing bounced the operator back");
            session.AssertConsoleClean();
        }

        /* The circuit is disposed a moment after its tab closes; the failure it logged came then. */
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        world.NavigationErrors.Skip(earlier).ShouldBeEmpty();
    }
}

/// <summary>A world that keeps every error the host logs about a navigation.</summary>
public sealed class NavigationLogWorld : AdminWorld
{
    private readonly NavigationLog _log = new();

    /// <summary>What the host logged about a navigation that threw or failed.</summary>
    public IReadOnlyCollection<string> NavigationErrors => _log.Errors;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services) => services.AddSingleton<ILoggerProvider>(_log);

    private sealed class NavigationLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _errors);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (logLevel >= LogLevel.Error
                    && (exception is NavigationException || message.Contains("Navigation failed", StringComparison.Ordinal)))
                {
                    errors.Enqueue($"{category}: {message} {exception?.GetType().Name} {Origin(exception)}");
                }
            }

            /// <summary>The screen the navigation came from: the first frame in the dashboard's own code.</summary>
            private static string? Origin(Exception? exception)
                => exception?.StackTrace?.Split('\n').FirstOrDefault(frame => frame.Contains("MMLib.Alvo.Admin.Components", StringComparison.Ordinal))?.Trim();
        }
    }
}
