using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MMLib.Alvo.Admin.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

// CA2012 reads every arranged JS call below as a ValueTask nobody awaited. They are NSubstitute's
// arrangement idiom: the call records an expectation and its result is never consumed as a task.
#pragma warning disable CA2012

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The circuit's one path into admin.js: one import, subscriptions that remove themselves, and one
/// answer to a circuit that has gone.
/// </summary>
public class AdminInteropTests
{
    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();
    private readonly IJSObjectReference _module = Substitute.For<IJSObjectReference>();

    public AdminInteropTests()
        => _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>())
            .Returns(new ValueTask<IJSObjectReference>(_module));

    [Fact]
    public async Task The_module_is_imported_once_however_many_calls_use_it()
    {
        var interop = new AdminInterop(_js);

        await interop.LockScrollAsync(true);
        await interop.DownloadAsync("export.json", "{}");
        await interop.SubscribeAsync("theme", _ => Task.CompletedTask);

        _js.ReceivedCalls().Count(call => call.GetArguments() is ["import", object?[] args]
            && Equals(args[0], AlvoAdminAssets.Module)).ShouldBe(1);
    }

    [Fact]
    public async Task A_subscription_forwards_its_event_to_the_handler()
    {
        string? received = null;
        var interop = new AdminInterop(_js);

        await interop.SubscribeAsync("move", value =>
        {
            received = value;
            return Task.CompletedTask;
        });

        var target = Arguments("subscribe").ShouldHaveSingleItem();
        target[0].ShouldBe("move");
        target[2].ShouldBe(JsEventHandler.Method);
        await ((DotNetObjectReference<JsEventHandler>)target[1]!).Value.Invoke("next");
        received.ShouldBe("next");
    }

    [Fact]
    public async Task A_subscription_removes_its_own_listener_once()
    {
        _module.InvokeAsync<int?>("subscribe", Arg.Any<object?[]?>()).Returns(new ValueTask<int?>(7));
        var interop = new AdminInterop(_js);

        var subscription = await interop.SubscribeAsync("theme", _ => Task.CompletedTask);
        await subscription.DisposeAsync();
        await subscription.DisposeAsync();

        Arguments("unsubscribe").ShouldHaveSingleItem().ShouldBe([7]);
    }

    [Theory]
    [InlineData(typeof(JSDisconnectedException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_circuit_that_has_gone_is_not_an_error(Type failure)
    {
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).ThrowsAsync(Gone(failure));
        var interop = new AdminInterop(_js);

        await Should.NotThrowAsync(() => interop.LockScrollAsync(true));
        (await interop.ThemeAsync()).ShouldBeNull();
        var subscription = await interop.SubscribeAsync("theme", _ => Task.CompletedTask);
        await Should.NotThrowAsync(subscription.DisposeAsync().AsTask);
        await Should.NotThrowAsync(interop.DisposeAsync().AsTask);
    }

    [Theory]
    [InlineData(typeof(JSDisconnectedException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_call_the_circuit_drops_mid_flight_is_not_an_error(Type failure)
    {
        _module.InvokeAsync<IJSVoidResult>(Arg.Any<string>(), Arg.Any<object?[]?>()).ThrowsAsync(Gone(failure));
        var interop = new AdminInterop(_js);

        await Should.NotThrowAsync(() => interop.DownloadAsync("export.json", "{}"));
    }

    /// <summary>A defect in the script is not a disconnect, and swallowing it would hide it.</summary>
    [Fact]
    public async Task A_script_error_surfaces()
    {
        _module.InvokeAsync<IJSVoidResult>(Arg.Any<string>(), Arg.Any<object?[]?>())
            .ThrowsAsync(new JSException("lockScroll is not a function"));
        var interop = new AdminInterop(_js);

        await Should.ThrowAsync<JSException>(() => interop.LockScrollAsync(true));
    }

    [Fact]
    public async Task The_circuit_ending_releases_a_module_it_imported_and_imports_none_it_did_not()
    {
        var unused = new AdminInterop(_js);
        await unused.DisposeAsync();
        _js.ReceivedCalls().ShouldBeEmpty();

        var used = new AdminInterop(_js);
        await used.LockScrollAsync(true);
        await used.DisposeAsync();
        await _module.Received(1).DisposeAsync();
    }

    private static Exception Gone(Type failure) => (Exception)Activator.CreateInstance(failure, "gone")!;

    private List<object?[]> Arguments(string identifier) => _module.ReceivedCalls()
        .Select(call => call.GetArguments())
        .Where(arguments => Equals(arguments[0], identifier))
        .Select(arguments => (object?[])arguments[1]!)
        .ToList();
}
