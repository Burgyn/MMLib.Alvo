using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary><c>Alvo:Extensions</c>: bound from configuration, refused at start when unusable, and the one source of the depth limit.</summary>
public class AlvoExtensionOptionsTests
{
    [Fact]
    public void The_default_limit_is_eight()
    {
        using var services = Container(new());

        services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.MaxDepth.ShouldBe(8);
        services.GetRequiredService<AlvoChainDepthLimit>().MaxDepth.ShouldBe(8);
    }

    [Fact]
    public void The_limit_is_bound_from_its_section()
    {
        using var services = Container(new() { ["Alvo:Extensions:MaxDepth"] = "3" });

        services.GetRequiredService<AlvoChainDepthLimit>().MaxDepth.ShouldBe(3);
    }

    [Fact]
    public void A_limit_below_one_is_refused_naming_the_key_and_its_variable()
    {
        using var services = Container(new() { ["Alvo:Extensions:MaxDepth"] = "0" });

        var refused = Should.Throw<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value);

        refused.Message.ShouldContain("Alvo:Extensions:MaxDepth");
        refused.Message.ShouldContain("Alvo__Extensions__MaxDepth");
    }

    private static ServiceProvider Container(Dictionary<string, string?> settings) => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
        .AddAlvoExtensions()
        .BuildServiceProvider();
}
