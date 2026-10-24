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
    public void A_delegate_that_returns_a_value_is_refused()
    {
        var refusal = Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => 42, typeof(AlvoCreated<AlvoRecord>)));

        refusal.Message.ShouldContain("Task");
        refusal.ParamName.ShouldBe("handler");
    }

    [Fact]
    public void A_task_or_value_task_with_a_result_is_refused_by_its_readable_name()
    {
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => Task.FromResult(1), typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("Task<Int32>");
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => ValueTask.FromResult(1), typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("ValueTask<Int32>");
    }

    [Fact]
    public void An_async_void_method_is_refused()
    {
        Action<AlvoCreated<AlvoRecord>> asyncVoid = AsyncVoidHandler;

        var refusal = Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate(asyncVoid, typeof(AlvoCreated<AlvoRecord>)));

        refusal.Message.ShouldContain("async void");
        refusal.ParamName.ShouldBe("handler");
    }

    [Fact]
    public void A_delegate_asking_for_another_verb_s_message_is_refused()
    {
        var refusal = Should.Throw<ArgumentException>(() =>
            HandlerInvoker.FromDelegate((AlvoUpdated<AlvoRecord> _) => Task.CompletedTask, typeof(AlvoCreated<AlvoRecord>)));

        refusal.Message.ShouldContain("AlvoCreated");
        refusal.ParamName.ShouldBe("handler");
    }

    [Fact]
    public async Task A_parameter_with_a_default_is_optional_and_gets_its_default_when_the_service_is_missing()
    {
        Clock? seen = new();
        var invoker = HandlerInvoker.FromDelegate(
            (AlvoCreated<AlvoRecord> _, Clock? clock = null) =>
            {
                seen = clock;
                return Task.CompletedTask;
            },
            typeof(AlvoCreated<AlvoRecord>));
        using var services = new ServiceCollection().BuildServiceProvider();

        invoker.Unresolvable(IsService()).ShouldBeEmpty();
        await invoker.InvokeAsync(services, _message, Context(), Ct);

        seen.ShouldBeNull();
    }

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
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(journal).BuildServiceProvider();
        var invoker = HandlerInvoker.FromClass<JournalHandler, AlvoCreated<AlvoRecord>>();

        await invoker.InvokeAsync(services, _message, Context(), Ct);
        await invoker.InvokeAsync(services, _message, Context(), Ct);

        journal.Handled.Distinct().Count().ShouldBe(2);
        journal.Disposed.ShouldBe(journal.Handled);
    }

    [Fact]
    public async Task A_registered_handler_class_comes_from_the_container_and_is_not_disposed_by_alvo()
    {
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(journal).AddSingleton<JournalHandler>().BuildServiceProvider();
        var invoker = HandlerInvoker.FromClass<JournalHandler, AlvoCreated<AlvoRecord>>();

        await invoker.InvokeAsync(services, _message, Context(), Ct);
        await invoker.InvokeAsync(services, _message, Context(), Ct);

        journal.Handled.ShouldBe([services.GetRequiredService<JournalHandler>().Id, services.GetRequiredService<JournalHandler>().Id]);
        journal.Disposed.ShouldBeEmpty();
    }

    [Fact]
    public void A_handler_class_reports_the_constructor_dependencies_the_container_cannot_resolve() =>
        HandlerInvoker.FromClass<JournalHandler, AlvoCreated<AlvoRecord>>().Unresolvable(IsService()).ShouldBe([typeof(Journal)]);

    [Fact]
    public async Task A_handler_class_with_a_constructor_the_container_can_satisfy_is_resolvable_and_built_with_it()
    {
        var invoker = HandlerInvoker.FromClass<TwoConstructorHandler, AlvoCreated<AlvoRecord>>();
        using var services = new ServiceCollection().BuildServiceProvider();

        invoker.Unresolvable(IsService()).ShouldBeEmpty();
        await invoker.InvokeAsync(services, _message, Context(), Ct);
    }

    [Fact]
    public void A_marked_constructor_is_the_one_checked() =>
        HandlerInvoker.FromClass<MarkedConstructorHandler, AlvoCreated<AlvoRecord>>().Unresolvable(IsService()).ShouldBe([typeof(Clock)]);

    [Fact]
    public void An_abstract_handler_class_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromClass<AbstractHandler, AlvoCreated<AlvoRecord>>()).Message.ShouldContain("abstract");

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

    private static async void AsyncVoidHandler(AlvoCreated<AlvoRecord> message) => await Task.Yield();

    internal sealed class Journal
    {
        private readonly List<Guid> _handled = [];
        private readonly List<Guid> _disposed = [];

        internal IReadOnlyList<Guid> Handled { get { lock (_handled) { return [.. _handled]; } } }

        internal IReadOnlyList<Guid> Disposed { get { lock (_disposed) { return [.. _disposed]; } } }

        internal void Handle(Guid id)
        {
            lock (_handled)
            {
                _handled.Add(id);
            }
        }

        internal void Dispose(Guid id)
        {
            lock (_disposed)
            {
                _disposed.Add(id);
            }
        }
    }

    internal sealed class JournalHandler(Journal journal) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>, IDisposable
    {
        internal Guid Id { get; } = Guid.NewGuid();

        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken)
        {
            journal.Handle(Id);
            return Task.CompletedTask;
        }

        public void Dispose() => journal.Dispose(Id);
    }

    private sealed class TwoConstructorHandler : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public TwoConstructorHandler()
        {
        }

        public TwoConstructorHandler(Clock clock) => ArgumentNullException.ThrowIfNull(clock);

        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MarkedConstructorHandler : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public MarkedConstructorHandler()
        {
        }

        [ActivatorUtilitiesConstructor]
        public MarkedConstructorHandler(Clock clock) => ArgumentNullException.ThrowIfNull(clock);

        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class AbstractHandler : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public abstract Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken);
    }
}
