using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// <see cref="AlvoApiOptions"/> fails fast at startup, with a message naming the option. Every fact here
/// asserts the <em>message</em> and not merely that something threw: a startup failure an operator cannot
/// read is barely better than the failure it replaced.
/// </summary>
/// <remarks>
/// The two that came from real breakage: <c>RoutePrefix = "/"</c> used to produce the pattern
/// <c>//owners</c> and an opaque <c>RoutePatternException</c> from deep inside routing, and a negative
/// <see cref="AlvoApiOptions.DefaultPageSize"/> used to turn every list into a 422 that blamed the caller.
/// </remarks>
public class AlvoApiOptionsValidatorTests
{
    [Fact]
    public void The_default_options_are_valid()
        => Validate(_ => { }).ShouldBeNull();

    /// <summary>
    /// The prefix that broke: normalization strips one slash from each end, so <c>"/"</c> collapses to
    /// nothing and mounts at the root — legal, and asserted here so the validator is not simply refusing
    /// every unusual prefix.
    /// </summary>
    [Fact]
    public void A_single_slash_route_prefix_mounts_at_the_root_and_is_accepted()
        => Validate(api => api.RoutePrefix = "/").ShouldBeNull();

    [Fact]
    public void An_empty_interior_segment_in_the_route_prefix_is_refused_naming_the_option()
        => ShouldFail(api => api.RoutePrefix = "/api//v1", nameof(AlvoApiOptions.RoutePrefix), "empty path segment");

    [Fact]
    public void A_route_parameter_in_the_route_prefix_is_refused_naming_the_option()
        => ShouldFail(api => api.RoutePrefix = "/api/{tenant}", nameof(AlvoApiOptions.RoutePrefix));

    [Fact]
    public void A_non_positive_default_page_size_is_refused_naming_the_option()
        => ShouldFail(api => api.DefaultPageSize = 0, nameof(AlvoApiOptions.DefaultPageSize));

    [Fact]
    public void A_negative_default_page_size_is_refused_naming_the_option()
        => ShouldFail(api => api.DefaultPageSize = -1, nameof(AlvoApiOptions.DefaultPageSize));

    [Fact]
    public void A_non_positive_max_page_size_is_refused_naming_the_option()
        => ShouldFail(api => api.MaxPageSize = 0, nameof(AlvoApiOptions.MaxPageSize));

    [Fact]
    public void A_default_page_size_larger_than_the_maximum_is_refused_naming_both()
        => ShouldFail(
            api =>
            {
                api.DefaultPageSize = 500;
                api.MaxPageSize = 200;
            },
            nameof(AlvoApiOptions.DefaultPageSize),
            nameof(AlvoApiOptions.MaxPageSize));

    [Fact]
    public void A_non_positive_body_size_bound_is_refused_naming_the_option()
        => ShouldFail(api => api.MaxRequestBodyBytes = 0, nameof(AlvoApiOptions.MaxRequestBodyBytes));

    [Fact]
    public void A_non_positive_payload_depth_bound_is_refused_naming_the_option()
        => ShouldFail(api => api.MaxPayloadDepth = 0, nameof(AlvoApiOptions.MaxPayloadDepth));

    [Fact]
    public void A_non_positive_payload_key_bound_is_refused_naming_the_option()
        => ShouldFail(api => api.MaxPayloadKeys = -3, nameof(AlvoApiOptions.MaxPayloadKeys));

    /// <summary>
    /// The validation must run at <b>startup</b>, not at the first request: that is the whole difference
    /// between an operator seeing the message above while deploying and a caller seeing a 500 in
    /// production. Driven through <see cref="IStartupValidator"/>, which is what
    /// <c>ValidateOnStart</c> registers and what the host runs.
    /// </summary>
    [Fact]
    public void A_misconfigured_option_fails_the_startup_validator()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite("Data Source=alvo-options-fact;Mode=Memory;Cache=Shared")
            .AddDataApi(api => api.RoutePrefix = "/api//v1"));

        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain(nameof(AlvoApiOptions.RoutePrefix));
    }

    /// <summary>
    /// And the same container starts when the options are sound, so the fact above is about the option
    /// rather than about a container that could never validate.
    /// </summary>
    [Fact]
    public void A_sound_configuration_passes_the_startup_validator()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite("Data Source=alvo-options-fact;Mode=Memory;Cache=Shared")
            .AddDataApi(api => api.RoutePrefix = "/api/v1"));

        using var provider = services.BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    /// <summary>
    /// Every failure at once, not the first: an operator fixing a misconfiguration one restart at a time
    /// is the experience §0 principle 4 exists to prevent.
    /// </summary>
    [Fact]
    public void Every_broken_option_is_reported_together()
        => ShouldFail(
            api =>
            {
                api.RoutePrefix = "/api//v1";
                api.MaxPageSize = 0;
                api.MaxPayloadKeys = 0;
            },
            nameof(AlvoApiOptions.RoutePrefix),
            nameof(AlvoApiOptions.MaxPageSize),
            nameof(AlvoApiOptions.MaxPayloadKeys));

    /// <summary>
    /// Asserts that <paramref name="configure"/> is refused and that the message mentions every one of
    /// <paramref name="expected"/> — a refusal whose message names nothing is a startup failure an operator
    /// cannot act on.
    /// </summary>
    private static void ShouldFail(Action<AlvoApiOptions> configure, params string[] expected)
    {
        var message = Validate(configure);

        message.ShouldNotBeNull("the options must be refused");
        foreach (var fragment in expected)
        {
            message.ShouldContain(fragment);
        }
    }

    /// <summary>
    /// The failure message the registered validator produces for <paramref name="configure"/>, or
    /// <see langword="null"/> when the options are valid. Resolved out of a real container so the fact
    /// covers the <em>registration</em> too — a validator nobody registered would pass every fact written
    /// against the class directly.
    /// </summary>
    private static string? Validate(Action<AlvoApiOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.AddDataApi(configure));

        using var provider = services.BuildServiceProvider();
        var options = new AlvoApiOptions();
        configure(options);

        var results = provider.GetServices<IValidateOptions<AlvoApiOptions>>()
            .Select(validator => validator.Validate(Options.DefaultName, options))
            .Where(result => result.Failed)
            .ToList();

        results.Count.ShouldBeLessThanOrEqualTo(1, "one validator, or a duplicate registration is reporting twice");
        return results.Count == 0 ? null : results[0].FailureMessage;
    }
}
