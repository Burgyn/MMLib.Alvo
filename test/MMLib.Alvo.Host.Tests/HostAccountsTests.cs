using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone host turns end-user sign-in on by configuration (S2 D9): a provider under <c>Alvo:Accounts:Providers</c>
/// registers and maps it; without one the image behaves exactly as it did before sign-in existed (Review Focus 4).
/// </summary>
public sealed class HostAccountsTests : IDisposable
{
    private const string Issuer = "https://alvo.example.test/auth/v1";

    private const string Entries = "/api/warehouses";

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"alvo-host-accounts-{Guid.NewGuid():N}.key");

    public HostAccountsTests() =>
        File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public async Task A_host_without_accounts_configuration_maps_no_auth_route_and_needs_no_key_file()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var token = await world.SendAnonymouslyAsync(HttpMethod.Post, "/auth/v1/token");
        using var metadata = await world.SendAnonymouslyAsync(HttpMethod.Get, "/auth/v1/.well-known/oauth-authorization-server");
        var document = await DocumentAsync(world);
        using var bearer = await SendWithAuthorizationAsync(world, "Bearer x");
        using var anonymous = await world.SendAnonymouslyAsync(HttpMethod.Get, Entries);

        token.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        metadata.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        document["paths"]!.AsObject().Select(path => path.Key).ShouldNotContain(path => path.StartsWith("/auth", StringComparison.Ordinal));
        SchemesOf(document).ShouldNotContain("alvoBearer");
        bearer.StatusCode.ShouldBe(anonymous.StatusCode, "without accounts an Authorization header is nobody's credential");
    }

    [Fact]
    public async Task A_host_with_a_google_provider_maps_sign_in()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: Accounts(_keyFile));

        using var metadata = await world.SendAnonymouslyAsync(HttpMethod.Get, "/auth/v1/.well-known/oauth-authorization-server");
        var document = await DocumentAsync(world);
        using var bearer = await SendWithAuthorizationAsync(world, "Bearer x");

        metadata.StatusCode.ShouldBe(HttpStatusCode.OK, await metadata.ReadTextAsync());
        (await metadata.ReadJsonObjectAsync())["issuer"]!.GetValue<string>().ShouldBe(Issuer);
        document["paths"]!.AsObject().Select(path => path.Key).ShouldContain("/auth/v1/token");
        SchemesOf(document).ShouldContain("alvoBearer");
        bearer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "with accounts on, a Bearer token is a person's or nobody's");
    }

    [Fact]
    public async Task A_host_with_a_provider_but_no_key_file_refuses_to_start()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(overrides: Accounts(keyFile: null)));

        refusal.Message.ShouldContain("Alvo:Secrets:EncryptionKeyFile");
    }

    /// <summary>An issuer alone switches nothing on: only a provider does, so no key file is demanded.</summary>
    [Fact]
    public async Task Accounts_settings_without_a_provider_switch_nothing_on()
    {
        await using var world = await AlvoHostWorld.StartAsync(
            overrides: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Alvo:Accounts:Issuer"] = Issuer });

        using var token = await world.SendAnonymouslyAsync(HttpMethod.Post, "/auth/v1/token");

        token.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public void Dispose() => File.Delete(_keyFile);

    private static Dictionary<string, string?> Accounts(string? keyFile)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:Accounts:Issuer"] = Issuer,
            ["Alvo:Accounts:Providers:Google:ClientIds:0"] = "client-1.apps.googleusercontent.com",
        };
        if (keyFile is not null)
        {
            settings["Alvo:Secrets:EncryptionKeyFile"] = keyFile;
        }

        return settings;
    }

    private static async Task<JsonObject> DocumentAsync(AlvoHostWorld world)
    {
        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.OpenApiDocumentPath);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonObjectAsync();
    }

    private static IEnumerable<string> SchemesOf(JsonObject document) =>
        document["components"]?["securitySchemes"]?.AsObject().Select(scheme => scheme.Key) ?? [];

    private static async Task<HttpResponseMessage> SendWithAuthorizationAsync(AlvoHostWorld world, string authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Entries);
        request.Headers.TryAddWithoutValidation("Authorization", authorization).ShouldBeTrue();
        return await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
