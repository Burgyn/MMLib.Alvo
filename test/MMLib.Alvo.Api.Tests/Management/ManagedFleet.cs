namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The one world every Management API read fact is measured over — a project that declares an
/// <c>access</c> block, two entities and role-differentiated rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second access-block fixture beside <c>managed-notes</c>, and the two measure different things.</b>
/// That one is the minimum a gate fact needs: one entity nobody reads and one level that names somebody.
/// A read fact needs more than admission — a schema whose entity set is a set, and rules that answer
/// differently for two declared roles, so a policy simulation has something to disagree with production
/// about.
/// </para>
/// <para>
/// <b><see cref="AlvoApiWorldSetup.MapBeforePriming"/> is not a stylistic choice here.</b> It is the only
/// arrangement in which the boot itself applies the descriptor, and the boot is the only path that appends
/// a <c>DescriptorVersion</c>. Pre-priming through <c>SchemaMigrationRunner</c> writes an applied snapshot
/// and no history, so every fact about a descriptor export or a revision list would measure an empty store.
/// </para>
/// </remarks>
internal static class ManagedFleet
{
    /// <summary>The descriptor's file name under the test project's <c>descriptors/</c> directory.</summary>
    internal const string Descriptor = "managed-fleet.alvo.json";

    /// <summary>The project's own name, as the descriptor declares it.</summary>
    internal const string Project = "managed-fleet";

    /// <summary>The management route prefix this project's facts address.</summary>
    internal const string Routes = "/management/projects/" + Project;

    /// <summary>The descriptor file as the running test can read it, for a fact comparing the export to it.</summary>
    internal static string DescriptorPath =>
        Path.Combine(AppContext.BaseDirectory, "descriptors", Descriptor);

    /// <summary>Starts the world, with the management surface mapped and the boot doing the priming.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    internal static Task<AlvoApiWorld> StartAsync(IReadOnlyList<TestApiKey> keys) =>
        AlvoApiWorld.FromDescriptorAsync(
            Descriptor, keys, new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true));
}
