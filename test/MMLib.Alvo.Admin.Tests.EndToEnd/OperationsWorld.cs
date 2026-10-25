using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field-service world with one handler on <c>regions</c> that records the code of every region it is handed.</summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the dispatcher runs in the background, as in production.</remarks>
public sealed class OperationsWorld : AdminWorld
{
    internal const string RegionSync = "region-sync";

    internal ConcurrentQueue<string> Synced { get; } = new();

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("regions", regions => regions.OnCreated(
            RegionSync,
            (AlvoCreated<AlvoRecord> created) => Synced.Enqueue((string)created.New["code"]!),
            configure: options => options.Description = "Copies every new region to the dispatch system."));
}
