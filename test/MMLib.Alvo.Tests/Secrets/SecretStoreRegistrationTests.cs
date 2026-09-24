using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;

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

    /// <summary>
    /// A pinned secret is found whatever case the deployment typed its key in.
    /// </summary>
    /// <remarks>
    /// Configuration keys are case-insensitive and <see cref="SecretName"/>'s grammar is lower-case only,
    /// so an ordinal dictionary would hold a name no read could ever ask for — the pinned value invisible
    /// to a read <em>and</em> un-shadowing to a write, which is a silent version of the one failure the
    /// layered store exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_configuration_key_in_the_wrong_case_still_finds_the_secret()
    {
        using var provider = Build(($"{AlvoSecretOptions.ConfigurationSection}:Values:AI.API-Key", "sk-test"));

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

    /// <summary>
    /// A key-encryption key written into configuration is refused at startup, by name.
    /// </summary>
    /// <remarks>
    /// This repository already refuses <c>Alvo__Admin__BootstrapPassword</c> for the same reason, and the
    /// reason is sharper here: a value in configuration is a value in an environment dump, a process listing
    /// and a crash report, and this one decrypts every secret the deployment holds.
    /// </remarks>
    [Fact]
    public void An_encryption_key_in_configuration_is_refused_and_says_what_to_mount()
    {
        using var provider = Build((AlvoSecretOptionsValidation.RefusedKey, "bm90LWEta2V5"));

        var refusal = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AlvoSecretOptions>>().Value).Message;

        refusal.ShouldContain(AlvoSecretOptionsValidation.RefusedKey);
        refusal.ShouldContain("EncryptionKeyFile");
    }

    /// <summary>And the path to a key file is not what is refused — it is the fix the refusal names.</summary>
    [Fact]
    public void A_path_to_a_key_file_is_accepted()
    {
        using var provider = Build(
            ($"{AlvoSecretOptions.ConfigurationSection}:EncryptionKeyFile", "/run/secrets/alvo-secret-key"));

        provider.GetRequiredService<IOptions<AlvoSecretOptions>>().Value
            .EncryptionKeyFile.ShouldBe("/run/secrets/alvo-secret-key");
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
