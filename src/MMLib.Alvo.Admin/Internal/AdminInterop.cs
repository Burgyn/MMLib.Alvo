using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The circuit's one way into <c>admin.js</c>: the module imported once, the <c>alvo:*</c> events subscribed to,
/// and the handful of imperative gestures a component cannot express in markup.
/// </summary>
/// <remarks>
/// <para>
/// <b>One path, not four</b> (docs/architecture/admin-dashboard-review.md, F-18). The palette, the theme toggle
/// and the import screen each imported their own copy of the module and wrote their own subscribe, unsubscribe
/// and dispose; the sheet reached the global <c>alvo.js</c> instead. Each had its own idea of which failures a
/// dead circuit throws, and only one of them knew all three.
/// </para>
/// <para>
/// <b>One disconnect policy.</b> A circuit can go at any await — a reload, a navigation, a dropped connection —
/// and it reports that as <see cref="JSDisconnectedException"/>, as <see cref="ObjectDisposedException"/> from
/// a module released under the call, or as <see cref="OperationCanceledException"/> from a call torn down in
/// flight. None of them is anything a person could act on, so every call here drops them and nothing else: a
/// <see cref="JSException"/> is a defect in the script, and it surfaces. <see cref="AdminProblem"/> treats an
/// <see cref="ObjectDisposedException"/> as a fault because on a screen it may be a genuine use-after-dispose;
/// here it can only be the module or a .NET reference released under a call, which is the circuit going.
/// </para>
/// <para>
/// <b>A failed import is logged and retried, never cached.</b> A module that did not load (the asset not
/// served, a network blip) is logged at error, as <see cref="AdminProblem"/> logs a fault, and the gesture that
/// needed it does nothing; the next call imports again. Keeping the failed import would leave the circuit's
/// keyboard dead for good over one bad moment, and rethrowing it from a component's after-render would end
/// the circuit.
/// </para>
/// <para>
/// <b>Scoped, so one per circuit.</b> In Blazor Server a scope is a circuit; the module is imported on the first
/// call that needs it and released when the circuit ends.
/// </para>
/// </remarks>
/// <param name="js">The circuit's JavaScript runtime.</param>
/// <param name="logger">Where a failed import is recorded.</param>
internal sealed partial class AdminInterop(IJSRuntime js, ILogger<AdminInterop> logger) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;

    /// <summary>
    /// Forwards the document's <c>alvo:<paramref name="name"/></c> event to <paramref name="handler"/> until the
    /// answer is disposed.
    /// </summary>
    /// <param name="name">The event, without its <c>alvo:</c> prefix.</param>
    /// <param name="handler">What the event does, with the value alvo.js sent along with it.</param>
    /// <returns>The subscription; disposing it removes the listener. Inert when the circuit has gone.</returns>
    public async Task<IAsyncDisposable> SubscribeAsync(string name, Func<string?, Task> handler)
    {
        var target = DotNetObjectReference.Create(new JsEventHandler(handler));
        var token = await QuietlyAsync<int?>(module => module.InvokeAsync<int?>(
            "subscribe", name, target, JsEventHandler.Method));
        return new Subscription(this, token, target);
    }

    /// <summary>Takes one count of the page's scroll lock, or gives one back.</summary>
    public Task LockScrollAsync(bool locked) => QuietlyAsync(module => module.InvokeVoidAsync("lockScroll", locked));

    /// <summary>Downloads text as a file, with no server round trip.</summary>
    public Task DownloadAsync(string name, string text)
        => QuietlyAsync(module => module.InvokeVoidAsync("download", name, text));

    /// <summary>A stream over the text of the streamed box <paramref name="id"/>; see <c>streamOf</c> in admin.js.</summary>
    /// <param name="id">The box's element id.</param>
    /// <returns>
    /// The stream — empty when the box is gone or over its ceiling (refused at the box instead) — or
    /// <see langword="null"/> when the circuit has gone.
    /// </returns>
    public Task<IJSStreamReference?> StreamOfAsync(string id)
        => QuietlyAsync<IJSStreamReference?>(module => module.InvokeAsync<IJSStreamReference?>("streamOf", id));

    /// <summary>What the streamed box <paramref name="id"/> holds now, as <c>alvo:measured</c> says it; see <c>measureOf</c>.</summary>
    /// <param name="id">The box's element id.</param>
    /// <returns>"&lt;lines&gt; &lt;filled&gt;", or <see langword="null"/> when the box or the circuit is gone.</returns>
    public Task<string?> MeasureOfAsync(string id)
        => QuietlyAsync<string?>(module => module.InvokeAsync<string?>("measureOf", id));

    /// <summary>Copies <paramref name="text"/> to the clipboard; see <c>copyText</c> in admin.js.</summary>
    public Task CopyAsync(string text) => QuietlyAsync(module => module.InvokeVoidAsync("copyText", text));

    /// <summary>Says the keyboard map now has a listener; see <c>markKeyboardReady</c> in admin.js.</summary>
    public Task MarkKeyboardReadyAsync() => QuietlyAsync(module => module.InvokeVoidAsync("markKeyboardReady"));

    /// <summary>Says the shell rendered interactively, providers included; see <c>markShellReady</c> in admin.js.</summary>
    public Task MarkShellReadyAsync() => QuietlyAsync(module => module.InvokeVoidAsync("markShellReady"));

    /// <summary>Focuses the <c>aria-selected</c> element inside <paramref name="container"/> and scrolls it into view.</summary>
    public Task FocusSelectedAsync(ElementReference container)
        => QuietlyAsync(module => module.InvokeVoidAsync("focusSelected", container));

    /// <summary>Focuses the first of <paramref name="selectors"/> as soon as one of them is on screen.</summary>
    /// <param name="selectors">CSS selectors, most wanted first; see <c>focusFirstOnceShown</c> in admin.js.</param>
    public Task FocusFirstOnceShownAsync(IReadOnlyList<string> selectors)
        => QuietlyAsync(module => module.InvokeVoidAsync("focusFirstOnceShown", selectors));

    /// <summary>
    /// Focuses the first of <paramref name="selectors"/> once no dialog is over the page: where focus goes after a
    /// confirm (spec §3.2), in the one order every screen uses. The confirm closes, the action's result is drawn, and
    /// then focus moves to the trigger, or, when the trigger is gone, to the row that took the item's place or the
    /// list's create action; never <c>&lt;body&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Called from the handler that closed the confirm, so the call reaches the browser before the render that closes
    /// it, and the browser waits for the dialog to go. A component drawn beside the confirm to do this after its own
    /// render was tried first: the extra render it caused, while the library was closing the dialog, left the dialog
    /// open for good.
    /// </remarks>
    /// <param name="selectors">CSS selectors, most wanted first; see <c>focusFirstOnceClosed</c> in admin.js.</param>
    public Task FocusFirstOnceClosedAsync(IReadOnlyList<string> selectors)
        => QuietlyAsync(module => module.InvokeVoidAsync("focusFirstOnceClosed", selectors));

    /// <summary>Keeps a growing thread's newest item in view; see <c>followNewest</c> in admin.js.</summary>
    public Task FollowNewestAsync(ElementReference thread)
        => QuietlyAsync(module => module.InvokeVoidAsync("followNewest", thread));

    /// <summary>The operator's offset from UTC, once <see cref="LearnUtcOffsetAsync"/> has read it from the browser.</summary>
    /// <remarks>
    /// Kept here, on the circuit's one path into the browser, because it is a fact about the browser and every screen
    /// of the circuit reads the same one; <see langword="null"/> until it is read, or when the circuit has gone.
    /// </remarks>
    public TimeSpan? UtcOffset { get; private set; }

    /// <summary>Reads the browser's offset from UTC once per circuit (<c>utcOffsetMinutes</c> in admin.js).</summary>
    /// <remarks>
    /// Read once rather than per instant: what the dashboard shows in local time is minutes away (a lockout's end),
    /// so a daylight-saving change inside that window is the one case it misreads, by the change.
    /// </remarks>
    /// <returns>Whether this call learned it, so the caller knows to draw again.</returns>
    public async Task<bool> LearnUtcOffsetAsync()
    {
        if (UtcOffset is not null)
        {
            return false;
        }

        /* Range-checked where it arrives: the value is the browser's word, and an offset no zone can have (beyond
           ±14 h) would make every time drawn from it throw during a render. Such a value is not kept, and the words
           stay in UTC, saying so. */
        var minutes = await QuietlyAsync<int?>(module => module.InvokeAsync<int?>("utcOffsetMinutes"));
        UtcOffset = OperatorTime.Usable(minutes is { } east ? TimeSpan.FromMinutes(east) : null);
        return UtcOffset is not null;
    }

    /// <summary>The theme in force, or <see langword="null"/> when the circuit has gone.</summary>
    public Task<string?> ThemeAsync() => QuietlyAsync<string?>(module => module.InvokeAsync<string?>("theme"));

    /// <summary>Flips the theme; answers the new one, or <see langword="null"/> when the circuit has gone.</summary>
    public Task<string?> ToggleThemeAsync()
        => QuietlyAsync<string?>(module => module.InvokeAsync<string?>("toggleTheme"));

    /// <summary>
    /// Releases the module, when the circuit ends — only one that loaded: an import still in flight or one that
    /// failed has nothing to release, and teardown is no place to report it again.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true } imported)
        {
            _module = null;
            try
            {
                await imported.Result.DisposeAsync();
            }
            catch (Exception exception) when (IsDisconnect(exception))
            {
            }
        }
    }

    /// <summary>
    /// Imported once, on first use: a circuit that never reaches a keyboard gesture never loads the module.
    /// </summary>
    /// <returns>The module, or <see langword="null"/> when it did not load; see the remarks.</returns>
    private async Task<IJSObjectReference?> ModuleAsync()
    {
        var import = _module ??= js.InvokeAsync<IJSObjectReference>("import", AlvoAdminAssets.Module).AsTask();
        try
        {
            return await import;
        }
        catch (Exception exception) when (ReferenceEquals(_module, import))
        {
            _module = null;
            if (!IsDisconnect(exception))
            {
                ImportFailed(logger, AlvoAdminAssets.Module, exception);
            }

            return null;
        }
        catch (Exception)
        {
            /* Another caller awaiting the same import has already forgotten and reported it. */
            return null;
        }
    }

    private async Task QuietlyAsync(Func<IJSObjectReference, ValueTask> call)
        => await QuietlyAsync<bool>(async module =>
        {
            await call(module);
            return true;
        });

    private async Task<T?> QuietlyAsync<T>(Func<IJSObjectReference, ValueTask<T>> call)
    {
        if (await ModuleAsync() is not { } module)
        {
            return default;
        }

        try
        {
            return await call(module);
        }
        catch (Exception exception) when (IsDisconnect(exception))
        {
            return default;
        }
    }

    /// <summary>The three ways a circuit that has gone reports itself; see the remarks.</summary>
    internal static bool IsDisconnect(Exception exception)
        => exception is JSDisconnectedException or ObjectDisposedException or OperationCanceledException;

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "The admin dashboard could not import {Module}; its keyboard and overlay gestures do nothing until an import succeeds.")]
    private static partial void ImportFailed(ILogger logger, string module, Exception exception);

    /// <summary>One listener, removed by the token admin.js answered for it.</summary>
    private sealed class Subscription(
        AdminInterop interop, int? token, DotNetObjectReference<JsEventHandler> target) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (token is { } subscribed)
            {
                await interop.QuietlyAsync(module => module.InvokeVoidAsync("unsubscribe", subscribed));
            }

            target.Dispose();
        }
    }
}
