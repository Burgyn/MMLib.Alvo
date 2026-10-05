using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A host over <c>cel-functions.alvo.json</c> with <c>normalizePhone</c> registered as the fact needs it.</summary>
internal static class CelFunctionsWorld
{
    /// <summary>The project's name, as the descriptor declares it.</summary>
    internal const string Project = "cel-functions";

    /// <summary>The summary the host registers, so discovery can be compared with it.</summary>
    internal const string Summary = "Keeps the digits and a leading plus of a phone number.";

    /// <summary>Gets a key that may read and write contacts.</summary>
    internal static TestApiKey Writer { get; } = new("contacts-writer", ["writer"], ["contacts:read", "contacts:write"]);

    /// <summary>Starts the world with Alvo's problem documents mapped.</summary>
    /// <param name="normalizePhone">What the host's <c>normalizePhone</c> does.</param>
    internal static Task<AlvoApiWorld> StartAsync(Func<string, string?> normalizePhone) =>
        AlvoApiWorld.FromDescriptorAsync(
            "cel-functions.alvo.json",
            [Writer],
            new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureServicesAfterAlvo: services => Register(services, normalizePhone)));

    /// <summary>Registers <c>normalizePhone</c> into <paramref name="services"/>, as a host's <c>AddAlvo().AddCelFunction(…)</c> would.</summary>
    /// <param name="services">The host's services, after <c>AddAlvo</c>.</param>
    /// <param name="normalizePhone">The implementation.</param>
    internal static void Register(IServiceCollection services, Func<string, string?> normalizePhone) =>
        new Builder(services).AddCelFunction("normalizePhone", (string phone) => normalizePhone(phone), Summary);

    /// <summary>The builder a host holds; <c>AddCelFunction</c> reads only <see cref="Services"/>.</summary>
    private sealed class Builder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
