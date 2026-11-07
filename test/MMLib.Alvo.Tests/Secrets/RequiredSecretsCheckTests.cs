using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>The boot's required-secret check by itself: names only, empty is missing, a failing store fails closed.</summary>
public sealed class RequiredSecretsCheckTests
{
    private const string Value = "s3cr3t-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_required_and_only_the_second_missing_is_refused_naming_it_alone()
    {
        var store = await StoreWithAsync(("first-secret", Value));

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Check(store, "first-secret", "second-secret").RefuseAsync(Ct));

        refusal.Message.ShouldContain("'second-secret'");
        refusal.Message.ShouldNotContain("first-secret");
        refusal.Message.ShouldNotContain(Value);
    }

    [Fact]
    public async Task An_empty_required_secret_is_missing()
    {
        var store = await StoreWithAsync(("blank-secret", string.Empty));

        (await Should.ThrowAsync<AlvoStartupRefusedException>(() => Check(store, "blank-secret").RefuseAsync(Ct))).Message.ShouldContain("'blank-secret'");
    }

    [Fact]
    public async Task A_throwing_store_refuses_naming_its_exception_type_and_never_its_message()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Check(new ThrowingStore(), "any-secret").RefuseAsync(Ct));

        refusal.Message.ShouldContain(typeof(IOException).FullName!);
        refusal.Message.ShouldNotContain("disk on fire");
    }

    [Fact]
    public void A_required_secret_name_outside_the_grammar_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(() => services.AddAlvo(alvo => alvo.AddRequiredSecret("Not A Name")));
    }

    [Fact]
    public void Each_call_registers_its_own_secret()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.AddRequiredSecret("first-secret").AddRequiredSecret("second-secret"));

        services.Where(service => service.ServiceType == typeof(RequiredSecret))
            .Select(service => ((RequiredSecret)service.ImplementationInstance!).Name.Value)
            .ShouldBe(["first-secret", "second-secret"]);
    }

    private static RequiredSecretsCheck Check(ISecretStore store, params string[] names) =>
        new([.. names.Select(name => new RequiredSecret(SecretName.Parse(name)))], store);

    private static async Task<InMemorySecretStore> StoreWithAsync(params (string Name, string Value)[] secrets)
    {
        var store = new InMemorySecretStore();
        foreach (var (name, value) in secrets.Where(secret => secret.Value.Length > 0))
        {
            await store.SetAsync(SecretName.Parse(name), value, Ct);
        }

        return store;
    }

    private sealed class ThrowingStore : ISecretStore
    {
        public bool CanWrite => false;

        public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default) => throw new IOException("disk on fire");

        public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
