using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The wiring fact for the chain-depth limit: a host that configures <c>Alvo:Extensions:MaxDepth</c> gets that
/// limit on the EF driver's write faces and on <see cref="IAlvoEvents"/>, not the built-in default of 8.
/// </summary>
/// <remarks>
/// The contract suite proves every store refuses beyond 8; only this proves the driver resolves the configured
/// limit from the container rather than falling back to the default, which would leave the setting inert.
/// </remarks>
public sealed class SqliteChainDepthWiringTests : IAsyncDisposable
{
    private const int ConfiguredMaxDepth = 3;

    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_write_deeper_than_the_configured_limit_is_refused_and_one_at_it_is_accepted()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, configure: ConfigureMaxDepth);

        var refused = await Should.ThrowAsync<AlvoChainDepthExceededException>(
            () => world.CreateAsync("notes", Note(world), AtDepth(world.Alice, ConfiguredMaxDepth + 1)));
        var created = await world.CreateAsync("notes", Note(world), AtDepth(world.Alice, ConfiguredMaxDepth));

        refused.Message.ShouldContain($"past the limit of {ConfiguredMaxDepth}");
        created["title"].ShouldBe("configured");
    }

    [Fact]
    public async Task A_publish_deeper_than_the_configured_limit_is_refused()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture, configure: ConfigureMaxDepth);
        var events = world.Services.GetRequiredService<IAlvoEvents>();

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => events.PublishAsync(
            "orders.approved", "orders/1", null, AtDepth(world.Alice, ConfiguredMaxDepth + 1),
            TestContext.Current.CancellationToken));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static void ConfigureMaxDepth(IServiceCollection services) =>
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alvo:Extensions:MaxDepth"] = ConfiguredMaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build());

    private static AlvoContext AtDepth(AlvoContext caller, int depth) =>
        caller with { Provenance = new AlvoProvenance { Depth = depth } };

    private static Dictionary<string, object?> Note(DataWorld world) => new()
    {
        ["owner_id"] = world.Alice.User.Value,
        ["tenant_id"] = world.Tenant.Value,
        ["title"] = "configured",
        ["label"] = "deep",
    };
}
