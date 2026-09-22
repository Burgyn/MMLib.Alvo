using Microsoft.Extensions.Options;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>
/// The read-only layer, running the same contract every store runs.
/// </summary>
/// <remarks>
/// It is the adapter for every store a platform already provides — Key Vault, a mounted K8s secret,
/// user-secrets, an environment variable — because each of those reaches Alvo through a configuration
/// provider the host added.
/// </remarks>
public sealed class ConfigurationSecretStoreContractTests : SecretStoreContractTests
{
    protected override bool SupportsWriting => false;

    protected override ISecretStore CreateStore() =>
        new ConfigurationSecretStore(new StaticOptions(new AlvoSecretOptions()));

    private sealed class StaticOptions(AlvoSecretOptions options) : IOptionsMonitor<AlvoSecretOptions>
    {
        public AlvoSecretOptions CurrentValue => options;

        public AlvoSecretOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<AlvoSecretOptions, string?> listener) => null;
    }
}

/// <summary>What the read-only layer answers, beyond the shared contract.</summary>
public class ConfigurationSecretStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_configured_value_is_read_by_name() =>
        (await Store(("one.two", "v")).GetAsync(SecretName.Parse("one.two"), Ct)).ShouldBe("v");

    [Fact]
    public void It_says_it_cannot_write() => Store().CanWrite.ShouldBeFalse();

    /// <summary>The refusal names where the value would have to change instead.</summary>
    [Fact]
    public async Task A_write_names_the_configuration_section()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            async () => await Store().SetAsync(SecretName.Parse("k"), "v", Ct));

        refusal.Message.ShouldContain(AlvoSecretOptions.ConfigurationSection);
    }

    /// <summary>
    /// A configuration key that is not a secret name is skipped rather than throwing.
    /// </summary>
    /// <remarks>
    /// Configuration is a place people mistype, and one bad key must not take the whole listing — and with it
    /// the settings screen — down.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_key_is_skipped_rather_than_breaking_the_listing()
    {
        var names = await Store(("Good.One", "v"), ("good.one", "v")).ListNamesAsync(Ct);

        names.Select(name => name.Value).ShouldBe(["good.one"]);
    }

    private static ConfigurationSecretStore Store(params (string Name, string Value)[] configured)
    {
        var options = new AlvoSecretOptions();
        foreach (var (name, value) in configured)
        {
            options.Values[name] = value;
        }

        return new ConfigurationSecretStore(new StaticOptions(options));
    }

    private sealed class StaticOptions(AlvoSecretOptions options) : IOptionsMonitor<AlvoSecretOptions>
    {
        public AlvoSecretOptions CurrentValue => options;

        public AlvoSecretOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<AlvoSecretOptions, string?> listener) => null;
    }
}
