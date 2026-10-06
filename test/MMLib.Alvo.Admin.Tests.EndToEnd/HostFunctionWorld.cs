using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The embedded-host sample's world on the shipped host: the vehicle-registry descriptor, plus what its README has a host
/// developer add in C# — <c>normalizeVin</c> (spec §11.1), here with a second function whose summary carries markup — and
/// a dev key so a scenario can write through the HTTP Data API (E11). Every expression verdict is recorded by
/// <see cref="RecordingWorld"/>, so a scenario waits for the verdict on exactly the text it inserted.
/// </summary>
/// <remarks>
/// The functions are registered through <see cref="AdminWorld"/>'s test-side <see cref="IAlvoBuilder"/> adapter: the shipped
/// host has no C# extension point, and this world adds none to it.
/// </remarks>
public sealed class HostFunctionWorld : RecordingWorld
{
    /// <summary>
    /// The summary the sample registers <c>normalizeVin</c> with (<c>SampleHost.NormalizeVinSummary</c>, quoted by its
    /// README) — the text the editor must show.
    /// </summary>
    internal const string NormalizeVinSummary =
        "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit.";

    /// <summary>A summary carrying markup, which the editor must render as text.</summary>
    internal const string MarkupSummary = "Adds <b>bold</b> emphasis: an exclamation mark at the end.";

    private const string KeyId = "e2e-functions";
    private const string KeySecret = "4f1d9c2b7a6e5d3c8b0a9f8e7d6c5b4a";

    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.VehicleRegistry;

    /// <summary>A client of the HTTP Data API, authenticated with this world's dev key.</summary>
    /// <returns>The client; the caller disposes it.</returns>
    public HttpClient Api()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.Add("X-Alvo-Api-Key", $"{KeyId}.{KeySecret}");
        return client;
    }

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings)
    {
        settings["Alvo:Auth:DevKeys:0:KeyId"] = KeyId;
        settings["Alvo:Auth:DevKeys:0:Secret"] = KeySecret;
        settings["Alvo:Auth:DevKeys:0:User"] = "5eed0000-0000-4000-8000-0000000000f1";
        settings["Alvo:Auth:DevKeys:0:Roles:0"] = "authenticated";
        settings["Alvo:Auth:DevKeys:0:Roles:1"] = "admin";
        settings["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read";
        settings["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write";
    }

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary)
        .AddCelFunction("shout", (string value) => value + "!", MarkupSummary);

    /// <summary>The sample's own <c>NormalizeVin</c>: its parameter is named <c>vin</c>, which the signature shows.</summary>
    private static string NormalizeVin(string vin) =>
        new string([.. vin.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
}
