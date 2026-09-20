namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The one world in which an anonymous caller reaches a management <em>write</em> rather than its gate.
/// </summary>
/// <remarks>
/// <b>A third access-block fixture, and it exists for exactly one thing the other two cannot show.</b>
/// <see cref="ManagedFleet"/> names three levels by role and <c>managed-notes</c> names one, so both answer a
/// caller with no credential the same way: <c>403</c>, at the gate. That hides every refusal behind it —
/// including "a key needs a stable identity to scope by", which is a <c>422</c> and not an authentication
/// failure at all. <c>managed-open</c> declares <c>developer</c> as the constant <c>true</c>, which is a
/// configuration a dev-mode deployment genuinely has, so the anonymous caller gets past admission and the
/// refusal under test is the one that answers.
/// </remarks>
internal static class ManagedOpen
{
    /// <summary>The descriptor's file name under the test project's <c>descriptors/</c> directory.</summary>
    internal const string Descriptor = "managed-open.alvo.json";

    /// <summary>The project's own name, as the descriptor declares it.</summary>
    internal const string Project = "managed-open";

    /// <summary>The management route prefix this project's facts address.</summary>
    internal const string Routes = "/management/projects/" + Project;

    /// <summary>Starts the world, with the management surface mapped and the boot doing the priming.</summary>
    /// <returns>The running world.</returns>
    internal static Task<AlvoApiWorld> StartAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            Descriptor, [], new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true));

    /// <summary>The descriptor the project currently exports, read by the anonymous caller itself.</summary>
    /// <param name="world">The running world.</param>
    /// <returns>The stored descriptor text.</returns>
    internal static async Task<string> CurrentAsync(AlvoApiWorld world) =>
        (await (await world.SendAsync(HttpMethod.Get, Routes + "/descriptor", key: null)).ReadJsonObjectAsync())
            ["descriptorJson"]!.GetValue<string>();
}
