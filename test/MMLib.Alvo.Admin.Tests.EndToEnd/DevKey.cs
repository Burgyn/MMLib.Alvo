namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A development API key for a world whose scenario writes through the HTTP Data API (E11): an admin of every entity, so
/// the write is judged by the before-hooks under test rather than refused by a rule.
/// </summary>
/// <param name="KeyId">The key's id, the part before the dot.</param>
/// <param name="Secret">The key's secret, the part after it.</param>
internal sealed record DevKey(string KeyId, string Secret)
{
    /// <summary>Adds the key to a world's settings.</summary>
    /// <param name="settings">The world's configuration.</param>
    public void Configure(IDictionary<string, string?> settings)
    {
        settings["Alvo:Auth:DevKeys:0:KeyId"] = KeyId;
        settings["Alvo:Auth:DevKeys:0:Secret"] = Secret;
        settings["Alvo:Auth:DevKeys:0:User"] = "5eed0000-0000-4000-8000-0000000000f1";
        settings["Alvo:Auth:DevKeys:0:Roles:0"] = "authenticated";
        settings["Alvo:Auth:DevKeys:0:Roles:1"] = "admin";
        settings["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read";
        settings["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write";
    }

    /// <summary>A client of the HTTP Data API at <paramref name="baseAddress"/>, authenticated with this key.</summary>
    /// <param name="baseAddress">The world's address.</param>
    /// <returns>The client; the caller disposes it.</returns>
    public HttpClient Client(string baseAddress)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        client.DefaultRequestHeaders.Add("X-Alvo-Api-Key", $"{KeyId}.{Secret}");
        return client;
    }
}
