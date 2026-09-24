using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using NSubstitute;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Who a stream run through the gateway runs as: the operator on every step, and nobody outside one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins</b> (reported 24 Sep 2026): an admin asked the assistant which entities there were,
/// the model called <c>get_schema</c>, and the tool answered that it had no permission. The gateway published
/// the operator once, at the top of an async iterator; an <see cref="AsyncLocal{T}"/> value assigned there is
/// gone after the iterator's first <c>yield</c>, because every later <c>MoveNextAsync</c> resumes it on the
/// consumer's execution context. The tools run from the second step on — after the model's first update — so
/// every one of them saw no caller.
/// </para>
/// <para>
/// <b>Measured over an <see cref="AsyncLocal{T}"/> accessor, never a substitute.</b> The fact that missed it
/// (<c>AssistantGatewayTests.A_turn_runs_with_the_operator_published</c>) substituted the accessor, and a
/// substituted property is a plain field that no execution context can hide — so it passed over the very
/// defect it was written to prevent. <see cref="AmbientAccessor"/> repeats the core's box semantics.
/// </para>
/// </remarks>
public sealed class ManagementGatewayOperatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step after the first still runs as the operator.</summary>
    [Fact]
    public async Task A_step_after_the_first_runs_as_the_operator()
    {
        var ambient = new AmbientAccessor();
        var gateway = Gateway(ambient);

        var seen = new List<AlvoPrincipal?>();
        await foreach (var principal in gateway.AsOperatorAsync(() => Observed(ambient), Ct))
        {
            seen.Add(principal);
        }

        seen.ShouldBe([Operator, Operator], "every step of the stream is where a tool may run");
    }

    /// <summary>The consumer, between two steps and after the last, sees nobody published.</summary>
    /// <remarks>
    /// Default-deny: the operator is published for the stream's own work, and whatever the consumer does with an
    /// update — render it, call something else — is not done as the operator.
    /// </remarks>
    [Fact]
    public async Task The_consumer_never_runs_as_the_operator()
    {
        var ambient = new AmbientAccessor();
        var gateway = Gateway(ambient);

        var between = new List<AlvoPrincipal?>();
        await foreach (var _ in gateway.AsOperatorAsync(() => Observed(ambient), Ct))
        {
            between.Add(ambient.Principal);
        }

        between.ShouldBe([null, null]);
        ambient.Principal.ShouldBeNull();
    }

    /// <summary>Work a step left running sees nobody once that step has ended.</summary>
    /// <remarks>
    /// The box semantics are what make this hold: restoring after a step clears the holder that step's
    /// execution context captured, so a continuation that outlives the step cannot keep the operator's
    /// authority. The stream here starts such a continuation on its first step and lets it read only after the
    /// consumer has the first update.
    /// </remarks>
    [Fact]
    public async Task Work_that_outlives_a_step_does_not_keep_the_operator()
    {
        var ambient = new AmbientAccessor();
        var gateway = Gateway(ambient);
        var release = new TaskCompletionSource();
        Task<AlvoPrincipal?>? straggler = null;

        await foreach (var _ in gateway.AsOperatorAsync(() => Leaving(ambient, release.Task, task => straggler = task), Ct))
        {
            release.TrySetResult();
            (await straggler!).ShouldBeNull("the step that started it had ended");
        }
    }

    /// <summary>The caller the resolver answers with.</summary>
    private static AlvoPrincipal Operator { get; } = new()
    {
        Context = AlvoContext.System(tenant: null),
        Scopes = new HashSet<ApiKeyScope>(),
        KeyId = "the-operator",
    };

    /// <summary>
    /// Yields who is published on its first step, and again on its second — the second is where the defect was.
    /// </summary>
    private static async IAsyncEnumerable<AlvoPrincipal?> Observed(
        IAlvoContextAccessor ambient, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return ambient.Principal;

        // A real await, so the second step resumes from a continuation as a streamed update does.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        yield return ambient.Principal;
    }

    /// <summary>Starts, on its one step, a read that waits for <paramref name="release"/> before it looks.</summary>
    private static async IAsyncEnumerable<int> Leaving(
        IAlvoContextAccessor ambient, Task release, Action<Task<AlvoPrincipal?>> started)
    {
        started(ReadAfterAsync(ambient, release));

        yield return 1;

        await Task.CompletedTask;
    }

    private static async Task<AlvoPrincipal?> ReadAfterAsync(IAlvoContextAccessor ambient, Task release)
    {
        await release.ConfigureAwait(false);

        return ambient.Principal;
    }

    private static ManagementGateway Gateway(IAlvoContextAccessor ambient)
    {
        var callers = Substitute.For<IAlvoAdminCallerResolver>();
        callers.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(Operator);

        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(
            Substitute.For<IAlvoManagement>(), people: null, callers, authentication, ambient);
    }

    /// <summary>
    /// The core's <c>AlvoContextAccessor</c>, repeated: an <see cref="AsyncLocal{T}"/> over a mutable box.
    /// </summary>
    /// <remarks>
    /// A copy because the real one is internal to <c>MMLib.Alvo</c>, which this assembly does not reference
    /// (an architecture test keeps the dashboard off the core). The box is the part that matters: an assignment
    /// clears the holder the current context sees before installing a new one, so a flow that captured the old
    /// holder stops seeing its caller. <c>MMLib.Alvo.Host.Tests</c> runs the same path over the real accessor.
    /// </remarks>
    private sealed class AmbientAccessor : IAlvoContextAccessor
    {
        private static readonly AsyncLocal<Holder> _current = new();

        public AlvoPrincipal? Principal
        {
            get => _current.Value?.Principal;
            set
            {
                if (_current.Value is { } holder)
                {
                    holder.Principal = null;
                }

                if (value is not null)
                {
                    _current.Value = new Holder { Principal = value };
                }
            }
        }

        private sealed class Holder
        {
            public AlvoPrincipal? Principal { get; set; }
        }
    }
}
