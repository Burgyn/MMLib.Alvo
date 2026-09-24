using Microsoft.JSInterop;
using MMLib.Alvo.Admin.Internal;
using NSubstitute;

// CA2012 reads every arranged JS call below as a ValueTask nobody awaited. They are NSubstitute's
// arrangement idiom: the call records an expectation and its result is never consumed as a task.
#pragma warning disable CA2012

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// One overlay's hold on the page: taken and given back once each. A circuit that has gone is
/// <see cref="AdminInterop"/>'s to answer, and <see cref="AdminInteropTests"/> covers it.
/// </summary>
public sealed class ScrollLockTests : IAsyncDisposable
{
    private readonly IJSObjectReference _module = Substitute.For<IJSObjectReference>();
    private readonly AdminInterop _interop;

    public ScrollLockTests()
    {
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>())
            .Returns(new ValueTask<IJSObjectReference>(_module));
        _interop = new AdminInterop(js, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminInterop>.Instance);
    }

    [Fact]
    public async Task A_second_hold_from_the_same_overlay_does_not_count_twice()
    {
        var scrollLock = new ScrollLock(_interop);

        await scrollLock.HoldAsync();
        await scrollLock.HoldAsync();

        Calls(true).ShouldBe(1);
        scrollLock.Held.ShouldBeTrue();
    }

    [Fact]
    public async Task A_release_gives_back_only_a_hold_that_was_taken()
    {
        var scrollLock = new ScrollLock(_interop);

        await scrollLock.ReleaseAsync();
        await scrollLock.HoldAsync();
        await scrollLock.ReleaseAsync();
        await scrollLock.ReleaseAsync();

        Calls(false).ShouldBe(1);
        scrollLock.Held.ShouldBeFalse();
    }

    public ValueTask DisposeAsync() => _interop.DisposeAsync();

    private int Calls(bool locked) => _module.ReceivedCalls()
        .Count(call => call.GetArguments() is ["lockScroll", object?[] args] && Equals(args[0], locked));
}
