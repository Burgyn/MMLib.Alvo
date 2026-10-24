using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class AlvoDeliveryOptionsTests
{
    [Fact]
    public void Sixteen_deliveries_run_at_once_by_default()
    {
        using var services = Container(new());

        services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value.MaxConcurrency.ShouldBe(16);
    }

    [Fact]
    public void The_concurrency_is_bound_from_its_section()
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = "4" });

        services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value.MaxConcurrency.ShouldBe(4);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("256", 256)]
    public void The_ends_of_one_to_256_are_accepted(string configured, int expected)
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = configured });

        services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value.MaxConcurrency.ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("257")]
    public void A_concurrency_outside_one_to_256_is_refused_naming_the_key_and_its_variable(string configured)
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = configured });

        var refused = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value);

        refused.Message.ShouldContain("Alvo:Deliveries:MaxConcurrency");
        refused.Message.ShouldContain("Alvo__Deliveries__MaxConcurrency");
    }

    [Fact]
    public void An_unusable_concurrency_is_refused_when_the_host_starts()
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = "0" });

        var refused = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        refused.Message.ShouldContain("Alvo:Deliveries:MaxConcurrency");
    }

    private static ServiceProvider Container(Dictionary<string, string?> settings) => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
        .AddAlvoExtensions()
        .BuildServiceProvider();
}
