using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>A handler delegate's parameters bind to the message, the context, the token, or a service — checked at the call and at boot.</summary>
public sealed class HandlerInvokerTests
{
    private static readonly AlvoCreated<AlvoRecord> _message = new() { New = AlvoRecord.Empty };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delegate_receives_the_message_the_context_the_token_and_its_services()
    {
        object? seen = null;
        var invoker = HandlerInvoker.FromDelegate(
            (AlvoCreated<AlvoRecord> message, AlvoEventContext context, Clock clock, CancellationToken token) =>
            {
                seen = (message, context.Subscriber, clock, token);
                return Task.CompletedTask;
            },
            typeof(AlvoCreated<AlvoRecord>));
        using var services = new ServiceCollection().AddSingleton<Clock>().BuildServiceProvider();

        await invoker.InvokeAsync(services, _message, Context(), Ct);

        seen.ShouldBe((_message, "on-created", services.GetRequiredService<Clock>(), Ct));
    }

    [Fact]
    public async Task A_value_task_and_a_void_delegate_are_both_handlers()
    {
        var calls = 0;
        using var services = new ServiceCollection().BuildServiceProvider();

        await HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => { calls++; return ValueTask.CompletedTask; }, typeof(AlvoCreated<AlvoRecord>))
            .InvokeAsync(services, _message, Context(), Ct);
        await HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => { calls++; }, typeof(AlvoCreated<AlvoRecord>))
            .InvokeAsync(services, _message, Context(), Ct);

        calls.ShouldBe(2);
    }

    [Fact]
    public void A_delegate_that_returns_a_value_is_refused() =>
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => 42, typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("Task");

    [Fact]
    public void A_delegate_asking_for_another_verb_s_message_is_refused() =>
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoUpdated<AlvoRecord> _) => Task.CompletedTask, typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("AlvoCreated");

    [Fact]
    public async Task The_handler_s_own_exception_escapes_unwrapped()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Func<AlvoCreated<AlvoRecord>, Task> throwsBeforeItsTask = _ => throw new TimeoutException("upstream");
        var invoker = HandlerInvoker.FromDelegate(throwsBeforeItsTask, typeof(AlvoCreated<AlvoRecord>));

        await Should.ThrowAsync<TimeoutException>(() => invoker.InvokeAsync(services, _message, Context(), Ct));
    }

    [Fact]
    public void A_service_the_container_cannot_resolve_is_reported()
    {
        var invoker = HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _, Clock clock) => Task.CompletedTask, typeof(AlvoCreated<AlvoRecord>));

        invoker.Unresolvable(IsService()).ShouldBe([typeof(Clock)]);
    }

    [Fact]
    public async Task A_handler_class_is_built_per_delivery_and_disposed_when_it_was_not_registered()
    {
        using var services = new ServiceCollection().AddSingleton<Clock>().BuildServiceProvider();
        var invoker = HandlerInvoker.FromClass<CountingHandler, AlvoCreated<AlvoRecord>>();

        await invoker.InvokeAsync(services, _message, Context(), Ct);

        CountingHandler.Handled.ShouldBeGreaterThanOrEqualTo(1);
        CountingHandler.Disposed.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void A_handler_class_reports_the_constructor_dependencies_the_container_cannot_resolve() =>
        HandlerInvoker.FromClass<CountingHandler, AlvoCreated<AlvoRecord>>().Unresolvable(IsService()).ShouldBe([typeof(Clock)]);

    private static AlvoEventContext Context() => new(
        new AlvoEvent
        {
            Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
            Subject = "tickets/1",
            PartitionKey = "tickets:1",
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        },
        "on-created",
        attempt: 1,
        Substitute.For<IAlvoSession>());

    private static IServiceProviderIsService IsService()
    {
        var isService = Substitute.For<IServiceProviderIsService>();
        isService.IsService(Arg.Any<Type>()).Returns(false);

        return isService;
    }

    internal sealed class Clock;

    private sealed class CountingHandler(Clock clock) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>, IDisposable
    {
        private static int _handled;
        private static int _disposed;

        internal static int Handled => Volatile.Read(ref _handled);

        internal static int Disposed => Volatile.Read(ref _disposed);

        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(clock);
            Interlocked.Increment(ref _handled);
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}
