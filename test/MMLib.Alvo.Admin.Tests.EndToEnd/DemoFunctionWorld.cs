namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop example as <c>scripts/demo-admin</c> boots it, with a dev key so a scenario can write through the HTTP
/// Data API and read back what the demo's before-hooks stored. No host function: the standalone demo has none (Task 16b).
/// </summary>
public sealed class DemoFunctionWorld : AdminWorld
{
    private static readonly DevKey _key = new("e2e-demo", "9b8a7c6d5e4f30211203f4e5d6c7b8a9");

    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;

    /// <summary>A client of the HTTP Data API, authenticated with this world's dev key.</summary>
    /// <returns>The client; the caller disposes it.</returns>
    public HttpClient Api() => _key.Client(BaseAddress);

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings) => _key.Configure(settings);
}
