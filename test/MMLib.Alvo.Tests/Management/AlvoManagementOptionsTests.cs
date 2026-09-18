using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// Where the Management API mounts, bound from <c>Alvo:Management</c> and refused at startup when it
/// cannot mount at all.
/// </summary>
public class AlvoManagementOptionsTests
{
    [Fact]
    public void A_host_that_says_nothing_mounts_management_under_slash_management() =>
        Resolve(settings: null).RoutePrefix.ShouldBe("/management");

    [Fact]
    public void The_prefix_binds_from_the_double_underscore_spelling_an_operator_writes() =>
        Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/admin-api" })
            .RoutePrefix.ShouldBe("/admin-api");

    [Fact]
    public void The_configuration_key_is_the_one_the_refusal_quotes() =>
        AlvoManagementOptionsConfiguration.RoutePrefixKey.ShouldBe(
            $"{AlvoManagementOptions.SectionName}:{nameof(AlvoManagementOptions.RoutePrefix)}");

    [Theory]
    [InlineData("/management//v1")]
    [InlineData("/management/{project}")]
    [InlineData("/management/*")]
    public void A_prefix_that_is_not_literal_path_text_is_refused_at_startup(string prefix)
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = prefix }));

        refusal.Message.ShouldContain(AlvoManagementOptionsConfiguration.RoutePrefixKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("  ")]
    public void A_prefix_that_reduces_to_nothing_is_refused_at_startup(string prefix)
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = prefix }));

        refusal.Message.ShouldContain(
            "would shadow an entity route at the root",
            Case.Sensitive,
            "the management surface owns literal segments the Data API's own entities could be named after");
    }

    [Fact]
    public void The_management_prefix_may_not_collide_with_the_data_api_prefix()
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/api" }));

        refusal.Message.ShouldContain(
            "would sit under the Data API's own prefix",
            Case.Sensitive,
            "two surfaces on one prefix is a route table nobody can reason about");
    }

    [Fact]
    public void A_prefix_nested_inside_the_data_api_prefix_is_refused_too() =>
        Should.Throw<OptionsValidationException>(
                () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/api/management" }))
            .Message.ShouldContain("would sit under the Data API's own prefix");

    [Fact]
    public void Every_refusal_is_reported_at_once_rather_than_the_first()
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/api//{x}" }));

        refusal.Failures.Count().ShouldBe(
            3, "an empty segment, a reserved character and the Data API collision are three separate fixes");
    }

    [Fact]
    public void A_data_api_prefix_that_cannot_mount_is_left_to_the_data_api_to_refuse() =>
        Resolve(
                settings: null,
                services => services.Configure<AlvoApiOptions>(api => api.RoutePrefix = "/api//v1"))
            .RoutePrefix.ShouldBe(
                "/management",
                "re-reporting the Data API's own refusal from here would name the wrong option and the wrong fix");

    private static AlvoManagementOptions Resolve(
        IDictionary<string, string?>? settings, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        services.AddAlvo();
        configure?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AlvoManagementOptions>>().Value;
    }
}
