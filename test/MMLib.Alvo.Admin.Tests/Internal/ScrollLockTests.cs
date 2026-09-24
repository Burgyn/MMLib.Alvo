using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MMLib.Alvo.Admin.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// One overlay's hold on the page: taken and given back once each, and quiet on a circuit that has gone.
/// </summary>
public class ScrollLockTests
{
    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();

    [Fact]
    public async Task A_second_hold_from_the_same_overlay_does_not_count_twice()
    {
        var scrollLock = new ScrollLock(_js);

        await scrollLock.HoldAsync();
        await scrollLock.HoldAsync();

        Calls(true).ShouldBe(1);
        scrollLock.Held.ShouldBeTrue();
    }

    [Fact]
    public async Task A_release_gives_back_only_a_hold_that_was_taken()
    {
        var scrollLock = new ScrollLock(_js);

        await scrollLock.ReleaseAsync();
        await scrollLock.HoldAsync();
        await scrollLock.ReleaseAsync();
        await scrollLock.ReleaseAsync();

        Calls(false).ShouldBe(1);
        scrollLock.Held.ShouldBeFalse();
    }

    [Theory]
    [InlineData(typeof(JSDisconnectedException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_circuit_that_has_gone_is_not_an_error(Type failure)
    {
        var thrown = (Exception)Activator.CreateInstance(failure, "gone")!;
        _js.InvokeAsync<IJSVoidResult>(Arg.Any<string>(), Arg.Any<object?[]?>()).ThrowsAsync(thrown);
        var scrollLock = new ScrollLock(_js);

        await Should.NotThrowAsync(scrollLock.HoldAsync);
        await Should.NotThrowAsync(scrollLock.ReleaseAsync);
    }

    private int Calls(bool locked) => _js.ReceivedCalls()
        .Count(call => call.GetArguments() is ["alvo.lockScroll", object?[] args] && Equals(args[0], locked));
}
