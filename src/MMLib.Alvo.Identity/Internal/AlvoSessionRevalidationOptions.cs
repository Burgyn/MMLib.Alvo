using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// How often an open Blazor circuit's session is re-checked against the store: thirty seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>A test seam, not a setting.</b> The internal key <c>Alvo:Admin:SessionRevalidationSeconds</c> exists so the
/// dashboard's end-to-end world can watch an open tab drop to sign-in in a couple of seconds instead of waiting out
/// the half minute. It is not an option, is on no public type, and no deployment has a reason to set it — the
/// credential limit's keys (<c>AlvoAdminCredentialLimit</c> in the host) are the same pattern.
/// </para>
/// <para>
/// <b>It can only shorten the interval.</b> Anything but a whole number from 1 to 30 is refused at start. A longer
/// interval would let a tab outlive a disable or a password change for longer than the design promises (design
/// 2026-09-27-f5-admin-set-password §4), so the knob is not a way to weaken that; a shorter one costs only a read
/// per open tab per interval.
/// </para>
/// </remarks>
internal sealed class AlvoSessionRevalidationOptions
{
    /// <summary>Where the test-only override is read from.</summary>
    internal const string Key = "Alvo:Admin:SessionRevalidationSeconds";

    /// <summary>The shipped interval, and the most the override may say.</summary>
    internal const int DefaultSeconds = 30;

    /// <summary>The interval, in whole seconds.</summary>
    public int Seconds { get; set; } = DefaultSeconds;

    /// <summary>The interval.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(Seconds);

    /// <summary>Registers the interval, read from configuration when there is any, and validated at start.</summary>
    /// <remarks>
    /// Configuration is resolved optionally: a service collection with none (a unit test's, a worker's) keeps the
    /// default rather than failing to build the circuit's provider.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    internal static void Register(IServiceCollection services)
        => services.AddOptions<AlvoSessionRevalidationOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                options.Read(provider.GetService<IConfiguration>()?[Key]))
            .Validate(options => options.Seconds is > 0 and <= DefaultSeconds, Refusal())
            .ValidateOnStart();

    /// <summary>Takes the configured text; anything that is not a whole number reads as zero, and is refused.</summary>
    /// <param name="configured">The configured value, or <see langword="null"/> for the default.</param>
    internal void Read(string? configured)
        => Seconds = configured is null ? DefaultSeconds
            : int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    /// <summary>The refusal, in the container spelling an operator types.</summary>
    private static string Refusal()
    {
        var variable = Key.Replace(":", "__", StringComparison.Ordinal);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Alvo cannot start: {variable} has to be a whole number from 1 to {DefaultSeconds}. It is a test seam that "
            + $"only shortens how often an open dashboard tab is re-checked; unset it for the default of {DefaultSeconds}.");
    }
}
