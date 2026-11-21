using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Sweeping;
using MMLib.Alvo.Tests.Accounts;
using System.Net;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// #527 over a real host: <c>AddAlvoAccounts</c> registers its four jobs on the one framework sweeper, and a sweep after
/// the retention deletes a signed-out session and its refresh tokens while a live one stays and still refreshes.
/// </summary>
public sealed class AccountsSweepTests
{
    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task Accounts_registers_four_jobs_on_the_one_framework_sweeper()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        world.Api.Services.GetServices<ISweepJob>().Select(job => job.Name).ShouldBe(
            ["inbound webhook ids", "command keys", "end-user sessions", "rotated refresh tokens", "app tokens", "invitations"],
            ignoreOrder: true);
        world.Api.Services.GetServices<IHostedService>().OfType<FrameworkSweeper>().Count().ShouldBe(1);
    }

    /// <summary>
    /// The host's own sweeper ticks on the clock too — advancing a day fires it — so the fact runs one sweep itself and
    /// judges the end state, whichever sweep did the work.
    /// </summary>
    [Fact]
    public async Task A_sweep_past_the_retention_forgets_a_signed_out_session_and_keeps_a_live_one()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, options => options.RetentionDays = 1);
        var (gone, _) = await world.SignInAsync("sub-1");
        var (_, kept) = await world.SignInAsync("sub-2");
        (await world.AsPersonAsync(HttpMethod.Post, "/auth/v1/logout", gone)).Dispose();
        world.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        using var refreshed = await world.RefreshAsync(kept);

        _ = await world.Api.Services.GetServices<IHostedService>().OfType<FrameworkSweeper>().Single()
            .SweepOnceAsync(TestContext.Current.CancellationToken);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.ReadTextAsync());
        (await world.CountAsync("_auth_sessions")).ShouldBe(1);
        (await world.CountAsync("_auth_refresh_tokens")).ShouldBe(2, "the live session's tip, and its parent inside the grace window");
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        _ = await world.Api.Services.GetServices<IHostedService>().OfType<FrameworkSweeper>().Single()
            .SweepOnceAsync(TestContext.Current.CancellationToken);
        (await world.CountAsync("_auth_refresh_tokens")).ShouldBe(1, "past the grace only the tip is left");
    }

    [Fact]
    public void The_id_token_window_is_five_minutes_of_age_and_two_of_skew()
        => AccountsSweepJobs.IdTokenWindow.ShouldBe(TimeSpan.FromMinutes(7));
}
