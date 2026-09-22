using Microsoft.Extensions.Options;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>
/// Configuration over a writable store: which layer answers, and what happens to a write the first one
/// would make invisible.
/// </summary>
public class LayeredSecretStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Configuration_wins_over_the_writable_store()
    {
        var writable = new WritableFake();
        await writable.SetAsync(Name("k"), "from-the-database", Ct);

        var store = Layered(writable, ("k", "from-configuration"));

        (await store.GetAsync(Name("k"), Ct)).ShouldBe("from-configuration");
    }

    [Fact]
    public async Task A_name_configuration_does_not_carry_falls_through()
    {
        var writable = new WritableFake();
        await writable.SetAsync(Name("k"), "from-the-database", Ct);

        (await Layered(writable, ("other", "x")).GetAsync(Name("k"), Ct)).ShouldBe("from-the-database");
    }

    /// <summary>
    /// A write to a name configuration shadows is refused by name.
    /// </summary>
    /// <remarks>
    /// The one failure this layering can produce, and the silent version is the worst kind: the operator
    /// saves a key, the screen says saved, and every request keeps using the old one.
    /// </remarks>
    [Fact]
    public async Task Writing_a_name_configuration_shadows_is_refused_by_name()
    {
        var store = Layered(new WritableFake(), ("k", "pinned"));

        var refusal = await Should.ThrowAsync<SecretShadowedException>(
            async () => await store.SetAsync(Name("k"), "ignored", Ct));

        refusal.Name.Value.ShouldBe("k");
        refusal.Message.ShouldContain(AlvoSecretOptions.ConfigurationSection);
    }

    /// <summary>Deleting one is refused for the same reason: it would not be the value anybody reads.</summary>
    [Fact]
    public async Task Deleting_a_name_configuration_shadows_is_refused_too()
    {
        var store = Layered(new WritableFake(), ("k", "pinned"));

        await Should.ThrowAsync<SecretShadowedException>(
            async () => await store.DeleteAsync(Name("k"), Ct));
    }

    [Fact]
    public async Task A_write_lands_in_the_writable_store()
    {
        var writable = new WritableFake();

        await Layered(writable, ("other", "x")).SetAsync(Name("k"), "v", Ct);

        (await writable.GetAsync(Name("k"), Ct)).ShouldBe("v");
    }

    [Fact]
    public async Task ListNames_is_the_union_of_both_layers()
    {
        var writable = new WritableFake();
        await writable.SetAsync(Name("b"), "v", Ct);

        var names = await Layered(writable, ("a", "v")).ListNamesAsync(Ct);

        names.Select(name => name.Value).OrderBy(value => value, StringComparer.Ordinal).ShouldBe(["a", "b"]);
    }

    /// <summary>A name both layers carry is one secret, not two.</summary>
    [Fact]
    public async Task ListNames_does_not_repeat_a_name_both_layers_carry()
    {
        var writable = new WritableFake();
        await writable.SetAsync(Name("k"), "v", Ct);

        (await Layered(writable, ("k", "pinned")).ListNamesAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public void With_no_writable_store_the_layer_cannot_write() =>
        Layered(writable: null, ("a", "v")).CanWrite.ShouldBeFalse();

    /// <summary>And says what this deployment would have to do, rather than failing as a missing service.</summary>
    [Fact]
    public async Task With_no_writable_store_a_write_names_what_is_missing()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            async () => await Layered(writable: null, ("a", "v")).SetAsync(Name("k"), "v", Ct));

        refusal.Message.ShouldContain("EncryptionKeyFile");
    }

    private static LayeredSecretStore Layered(IWritableSecretStore? writable, params (string Name, string Value)[] configured)
    {
        var options = new AlvoSecretOptions();
        foreach (var (name, value) in configured)
        {
            options.Values[name] = value;
        }

        return new LayeredSecretStore(
            new ConfigurationSecretStore(new StaticOptions(options)), writable);
    }

    private static SecretName Name(string value) => SecretName.Parse(value);

    /// <summary>The fake, marked writable so the layered store will use it.</summary>
    private sealed class WritableFake : IWritableSecretStore
    {
        private readonly InMemorySecretStore _inner = new();

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

    /// <summary>The bound options, without a host to bind them.</summary>
    private sealed class StaticOptions(AlvoSecretOptions options) : IOptionsMonitor<AlvoSecretOptions>
    {
        public AlvoSecretOptions CurrentValue => options;

        public AlvoSecretOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<AlvoSecretOptions, string?> listener) => null;
    }
}
