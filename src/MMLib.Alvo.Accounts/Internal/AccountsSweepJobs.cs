using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Sweeping;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The accounts' share of the framework sweep (#527): four <see cref="ISweepJob"/>s over the account store, each turning
/// <see cref="AlvoAccountsOptions"/> into the instants its store call binds. Registered by <c>AddAlvoAccounts</c> beside
/// the one framework sweeper, which runs them hourly in bounded batches.
/// </summary>
/// <remarks>
/// <b>The ID-token window is kept.</b> A session's <c>subject_token_id</c> is what makes an ID token single-use, and an ID
/// token is accepted for at most <see cref="IdTokenWindow"/> after its <c>iat</c>; no session younger than that is deleted,
/// whatever its state — an explicit guard beside a retention of at least a day (spec D10).
/// </remarks>
internal static class AccountsSweepJobs
{
    /// <summary>How long an ID token can still start a session: <c>iat</c> at most 5 minutes old, plus 2 minutes of skew.</summary>
    internal static TimeSpan IdTokenWindow { get; } = TimeSpan.FromMinutes(7);

    /// <summary>Registers the four jobs and the framework sweeper.</summary>
    /// <param name="services">The host's services.</param>
    internal static void Add(IServiceCollection services)
    {
        services.AddAlvoSweepJob<SessionsJob>()
            .AddAlvoSweepJob<RotatedRefreshTokensJob>()
            .AddAlvoSweepJob<AppTokensJob>()
            .AddAlvoSweepJob<InvitationsJob>();
    }

    /// <summary>Sessions revoked, expired or idle past the retention, with their refresh tokens.</summary>
    private sealed class SessionsJob(IAccountStore store, IOptions<AlvoAccountsOptions> options) : ISweepJob
    {
        public string Name => "end-user sessions";

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct)
        {
            var settings = options.Value;
            var past = now - TimeSpan.FromDays(settings.RetentionDays);
            return store.SweepSessionsAsync(
                new SessionSweep(past, past, past - TimeSpan.FromDays(settings.RefreshIdleDays), now - IdTokenWindow, limit), ct);
        }
    }

    /// <summary>Refresh tokens rotated longer ago than the grace window, plus a minute's margin.</summary>
    private sealed class RotatedRefreshTokensJob(IAccountStore store, IOptions<AlvoAccountsOptions> options) : ISweepJob
    {
        public string Name => "rotated refresh tokens";

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
            store.SweepRotatedRefreshTokensAsync(
                now - TimeSpan.FromSeconds(options.Value.RefreshReuseGraceSeconds) - TimeSpan.FromMinutes(1), limit, ct);
    }

    /// <summary>Personal and service tokens revoked or expired past the retention.</summary>
    private sealed class AppTokensJob(IAccountStore store, IOptions<AlvoAccountsOptions> options) : ISweepJob
    {
        public string Name => "app tokens";

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
            store.SweepAppTokensAsync(now - TimeSpan.FromDays(options.Value.RetentionDays), limit, ct);
    }

    /// <summary>Invitations revoked, expired or used up past the retention.</summary>
    private sealed class InvitationsJob(IAccountStore store, IOptions<AlvoAccountsOptions> options) : ISweepJob
    {
        public string Name => "invitations";

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
            store.SweepInvitationsAsync(now - TimeSpan.FromDays(options.Value.RetentionDays), limit, ct);
    }
}
