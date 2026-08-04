using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// One started database plus the before-hook invocations its writes made, as
/// <see cref="AlvoDataBeforeHookTests"/> asks for them.
/// </summary>
/// <remarks>
/// Linked into both engine test projects rather than copied, for the reason <see cref="DifferentialProbe"/>
/// is: the question the inherited suite asks is engine-agnostic, and two per-engine copies of the counter are
/// two chances for the engines to stop being asked the same one.
/// </remarks>
/// <param name="data">The data port the fixture built.</param>
/// <param name="runs">The recorder the container was decorated with.</param>
internal sealed class AlvoDataBeforeHookWorld(IAlvoData data, BeforeHookRunRecorder runs) : IAlvoDataBeforeHookWorld
{
    public IAlvoData Data { get; } = data;

    public IReadOnlyList<DataOperation> HookRuns => runs.Operations;
}

/// <summary>
/// The product's own <see cref="IBeforeHookRunner"/> with a call log around it — a decorator, never a
/// substitute.
/// </summary>
/// <remarks>
/// <para>
/// <b>It delegates, so every fact above it is a fact about the shipped pipeline.</b> A fake runner would make
/// the suite's <c>mutate</c> and <c>reject</c> facts assertions about the fake, which is the one thing a
/// contract suite must not become. All this adds is the answer to "how many times were you asked", which the
/// row a write returns cannot give: a before-hook is pure, so a second run over one candidate produces the
/// first run's value.
/// </para>
/// <para>
/// <b>The inner runner is built from the registration the container already had</b>, rather than constructed
/// here, because the core's implementation is <see langword="internal"/> to <c>MMLib.Alvo</c> — and because
/// naming it here would let this decorator wrap a different runner than the one a host resolves.
/// </para>
/// </remarks>
/// <param name="inner">The runner every call is forwarded to — the one the container had registered.</param>
internal sealed class BeforeHookRunRecorder(IBeforeHookRunner inner) : IBeforeHookRunner
{
    private readonly List<DataOperation> _operations = [];

    /// <summary>Every operation this runner was asked about, in call order.</summary>
    /// <remarks>
    /// Copied out under the lock rather than exposed directly: a write path may run on any thread, and the
    /// suite's own facts read this while nothing else is in flight only because they await their writes first —
    /// which is not a property this type can rely on for a future concurrent fact.
    /// </remarks>
    internal IReadOnlyList<DataOperation> Operations
    {
        get
        {
            lock (_operations)
            {
                return [.. _operations];
            }
        }
    }

    public IReadOnlyDictionary<string, object?> Run(
        string entity,
        DataOperation operation,
        AlvoRecord candidate,
        AlvoRecord? previous,
        AlvoContext context,
        DateTimeOffset now)
    {
        lock (_operations)
        {
            _operations.Add(operation);
        }

        return inner.Run(entity, operation, candidate, previous, context, now);
    }
}

/// <summary>Replaces the container's <see cref="IBeforeHookRunner"/> with a recorder wrapped around it.</summary>
internal static class BeforeHookRecording
{
    /// <summary>
    /// Decorates whatever <see cref="IBeforeHookRunner"/> the collection already holds, and answers with the
    /// recorder the suite reads its counts from.
    /// </summary>
    /// <remarks>
    /// The existing descriptor is removed and re-registered as a factory, which is the only shape that works
    /// here: the core's runner is <see langword="internal"/>, so the wrapper has to build it from the
    /// <see cref="ServiceDescriptor.ImplementationType"/> the container already recorded rather than by naming
    /// the type. <see cref="ActivatorUtilities.CreateInstance(IServiceProvider, Type, object[])"/> then
    /// injects its own dependencies exactly as the container would have.
    /// </remarks>
    /// <param name="services">The collection <c>AddAlvo</c> has already registered the runner on.</param>
    internal static void Decorate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registered = services.Last(service => service.ServiceType == typeof(IBeforeHookRunner));
        var implementation = registered.ImplementationType ?? throw new InvalidOperationException(
            "The registered IBeforeHookRunner carries no implementation type, so it cannot be decorated. If the "
            + "core switched to a factory registration, wrap that factory here instead.");

        services.Remove(registered);
        services.AddSingleton<IBeforeHookRunner>(provider => new BeforeHookRunRecorder(
            (IBeforeHookRunner)ActivatorUtilities.CreateInstance(provider, implementation)));
    }

    /// <summary>
    /// The recorder the container resolved, so the suite reads the counts of the very instance the writes go
    /// through rather than of a second one built beside it.
    /// </summary>
    /// <param name="provider">The container the fixture built.</param>
    internal static BeforeHookRunRecorder RecorderOf(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.GetRequiredService<IBeforeHookRunner>() as BeforeHookRunRecorder
            ?? throw new InvalidOperationException(
                $"The container's {nameof(IBeforeHookRunner)} is not the recorder, so nothing is counting hook "
                + $"runs. Pass {nameof(Decorate)} as the fixture's 'configure' callback.");
    }
}
