using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with one handler on <c>regions</c> that records the code of every region it is handed, and one
/// nightly schedule that counts its runs.
/// </summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the dispatcher runs in the background, as in production.</remarks>
public sealed class OperationsWorld : AdminWorld
{
    internal const string RegionSync = "region-sync";

    internal const string NightlyRoll = "nightly-roll";

    internal ConcurrentQueue<string> Synced { get; } = new();

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("regions", regions => regions.OnCreated(
            RegionSync,
            (AlvoCreated<AlvoRecord> created) => Synced.Enqueue((string)created.New["code"]!),
            configure: options => options.Description = "Copies every new region to the dispatch system."))
        .AddSchedule<RollSlots>(NightlyRoll, "0 2 * * *", "Europe/Bratislava", options => options.Description = "Rolls the next day's slots.");

    /// <summary>The nightly job: it only counts that it ran.</summary>
    internal sealed class RollSlots : IAlvoScheduledJob
    {
        private static int _runs;

        /// <summary>Gets how many times any instance of the job ran.</summary>
        internal static int Runs => Volatile.Read(ref _runs);

        /// <inheritdoc/>
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Task.CompletedTask;
        }
    }
}
