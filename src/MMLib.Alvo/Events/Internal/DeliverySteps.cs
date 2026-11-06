using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The steps of one attempt of one delivery: the completed steps are read once, lazily; a step found there returns its
/// stored result, any other runs its work and is recorded first-writer-wins, after the work returned.
/// </summary>
/// <remarks>
/// <para>
/// The record is written with <see cref="CancellationToken.None"/>: a step whose work finished is recorded even when the
/// attempt's token fired meanwhile, so the retry does not run it again.
/// </para>
/// <para>
/// <b>A sleep is a step whose result is its wake instant.</b> Once that instant has passed the call returns; until then it
/// sets <see cref="SleepRequested"/> and throws <see cref="StepSleep"/>. The runner reads the flag however the handler
/// ended, so a handler that swallows the signal still sleeps; and every later step call of the attempt throws the signal
/// again without running or recording anything, so no step runs before its wake time.
/// </para>
/// </remarks>
internal sealed class DeliverySteps(IDeliveryStore store, Guid eventId, string subscriber, TimeProvider time) : IAlvoSteps
{
    internal const int MaxSteps = 1000;
    internal const int MaxResultBytes = 256 * 1024;

    private const string NoValue = "null";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private Task<Dictionary<string, DeliveryStep>>? _completed;
    private DateTimeOffset? _sleepRequested;

    /// <summary>Gets the instant this attempt asked to sleep until, or <see langword="null"/> when it did not.</summary>
    internal DateTimeOffset? SleepRequested
    {
        get
        {
            lock (_gate)
            {
                return _sleepRequested;
            }
        }
    }

    public async Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        Claim(name);
        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            return Deserialize<T>(stored.Result);
        }

        var value = await work(cancellationToken).ConfigureAwait(false);
        var recorded = await RecordAsync(name, Serialize(value)).ConfigureAwait(false);

        return Deserialize<T>(recorded.Result);
    }

    public async Task RunAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        Claim(name);
        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        await work(cancellationToken).ConfigureAwait(false);
        await RecordAsync(name, NoValue).ConfigureAwait(false);
    }

    public async Task SleepUntilAsync(string name, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        Claim(name);
        var wake = await StoredAsync(name, cancellationToken).ConfigureAwait(false) is { } stored
            ? Deserialize<DateTimeOffset>(stored.Result)
            : Deserialize<DateTimeOffset>((await RecordAsync(name, Serialize(at.ToUniversalTime())).ConfigureAwait(false)).Result);
        if (time.GetUtcNow() < wake)
        {
            Sleep(wake);
        }
    }

    private void Sleep(DateTimeOffset wake)
    {
        lock (_gate)
        {
            _sleepRequested = wake;
        }

        throw new StepSleep();
    }

    private void Claim(string name)
    {
        StepNames.Check(name);
        lock (_gate)
        {
            if (_sleepRequested is not null)
            {
                throw new StepSleep();
            }

            if (_used.Count >= MaxSteps)
            {
                throw new InvalidOperationException($"A delivery runs at most {MaxSteps} steps; '{name}' would be one more.");
            }

            if (!_used.Add(name))
            {
                throw new InvalidOperationException($"The step '{name}' already ran in this delivery; a step name is used once per run.");
            }
        }
    }

    private async Task<DeliveryStep?> StoredAsync(string name, CancellationToken ct)
    {
        var completed = await CompletedAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            return completed.GetValueOrDefault(name);
        }
    }

    private Task<Dictionary<string, DeliveryStep>> CompletedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_completed is not { IsFaulted: false, IsCanceled: false })
            {
                _completed = LoadAsync(ct);
            }

            return _completed;
        }
    }

    private async Task<Dictionary<string, DeliveryStep>> LoadAsync(CancellationToken ct)
    {
        var steps = await store.StepsAsync(eventId, subscriber, ct).ConfigureAwait(false);

        return steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
    }

    private async Task<DeliveryStep> RecordAsync(string name, string result)
    {
        var step = new DeliveryStep { EventId = eventId, Subscriber = subscriber, Name = name, Result = result, CompletedAt = time.GetUtcNow() };
        var recorded = await store.RecordStepAsync(step, CancellationToken.None).ConfigureAwait(false);
        var completed = await CompletedAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            completed[name] = recorded;
        }

        return recorded;
    }

    private static string Serialize<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        if (json.Length > MaxResultBytes)
        {
            throw new InvalidOperationException(
                $"A step's result is at most {MaxResultBytes / 1024} KiB of JSON; this one is {json.Length} bytes. Store the value elsewhere and return its key.");
        }

        return System.Text.Encoding.UTF8.GetString(json);
    }

    private static T Deserialize<T>(string result) => JsonSerializer.Deserialize<T>(result, JsonSerializerOptions.Web)!;
}
