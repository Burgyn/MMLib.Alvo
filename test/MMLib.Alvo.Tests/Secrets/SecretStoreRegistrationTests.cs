using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>
/// What <c>AddAlvo</c> gives a host that wired nothing: a store, reading the configuration section, that says
/// it cannot write.
/// </summary>
/// <remarks>
/// The registration is the half neither store's own tests can reach. A layered store that was never composed,
/// an options section bound under the wrong path, or a writable half demanded rather than asked for would all
/// leave both stores green and every caller broken.
/// </remarks>
public class SecretStoreRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A deployment that mounted nothing still resolves a store rather than failing to build one.</summary>
    [Fact]
    public void A_host_that_registered_no_writable_store_still_resolves_one()
    {
        using var provider = Build();

        provider.GetRequiredService<ISecretStore>().CanWrite.ShouldBeFalse();
    }

    /// <summary>And the section it binds is the one the documentation names.</summary>
    [Fact]
    public async Task A_value_under_the_configuration_section_is_read_by_name()
    {
        using var provider = Build(($"{AlvoSecretOptions.ConfigurationSection}:Values:ai.api-key", "sk-test"));

        var value = await provider.GetRequiredService<ISecretStore>().GetAsync(SecretName.Parse("ai.api-key"), Ct);

        value.ShouldBe("sk-test");
    }

    /// <summary>A writable store a driver registered is the layer behind configuration.</summary>
    [Fact]
    public async Task A_registered_writable_store_is_the_layer_behind_configuration()
    {
        var services = Services();
        services.AddSingleton<IWritableSecretStore, WritableFake>();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISecretStore>();

        store.CanWrite.ShouldBeTrue();
        await store.SetAsync(SecretName.Parse("k"), "v", Ct);
        (await store.GetAsync(SecretName.Parse("k"), Ct)).ShouldBe("v");
    }

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
        => Services(settings).BuildServiceProvider();

    private static ServiceCollection Services(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlvo();

        return services;
    }

    /// <summary>Stands in for the driver-registered store the core never references.</summary>
    private sealed class WritableFake : IWritableSecretStore
    {
        private readonly Testing.Secrets.InMemorySecretStore _inner = new();

        public bool CanWrite => true;

        public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default) =>
            _inner.GetAsync(name, ct);

        public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) =>
            _inner.SetAsync(name, value, ct);

        public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) =>
            _inner.DeleteAsync(name, ct);

        public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) =>
            _inner.ListNamesAsync(ct);
    }
}
