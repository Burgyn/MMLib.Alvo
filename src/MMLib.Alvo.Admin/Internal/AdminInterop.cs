using Microsoft.AspNetCore.Components;
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
/// <see cref="JSException"/> is a defect in the script, and it surfaces.
/// </para>
/// <para>
/// <b>Scoped, so one per circuit.</b> In Blazor Server a scope is a circuit; the module is imported on the first
/// call that needs it and released when the circuit ends.
/// </para>
/// </remarks>
/// <param name="js">The circuit's JavaScript runtime.</param>
internal sealed class AdminInterop(IJSRuntime js) : IAsyncDisposable
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

    /// <summary>Says the keyboard map now has a listener; see <c>markKeyboardReady</c> in admin.js.</summary>
    public Task MarkKeyboardReadyAsync() => QuietlyAsync(module => module.InvokeVoidAsync("markKeyboardReady"));

    /// <summary>Moves focus into an input and selects its text, so what is typed next replaces it.</summary>
    public Task FocusAndSelectAsync(ElementReference input)
        => QuietlyAsync(module => module.InvokeVoidAsync("focusAndSelect", input));

    /// <summary>Focuses the <c>aria-selected</c> element inside <paramref name="container"/> and scrolls it into view.</summary>
    public Task FocusSelectedAsync(ElementReference container)
        => QuietlyAsync(module => module.InvokeVoidAsync("focusSelected", container));

    /// <summary>The theme in force, or <see langword="null"/> when the circuit has gone.</summary>
    public Task<string?> ThemeAsync() => QuietlyAsync<string?>(module => module.InvokeAsync<string?>("theme"));

    /// <summary>Flips the theme; answers the new one, or <see langword="null"/> when the circuit has gone.</summary>
    public Task<string?> ToggleThemeAsync()
        => QuietlyAsync<string?>(module => module.InvokeAsync<string?>("toggleTheme"));

    /// <summary>Releases the module, when the circuit ends.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await QuietlyAsync(module => module.DisposeAsync());
        }
    }

    /// <summary>
    /// Imported once, on first use: a circuit that never reaches a keyboard gesture never loads the module.
    /// </summary>
    private Task<IJSObjectReference> ModuleAsync()
        => _module ??= js.InvokeAsync<IJSObjectReference>("import", AlvoAdminAssets.Module).AsTask();

    private async Task QuietlyAsync(Func<IJSObjectReference, ValueTask> call)
        => await QuietlyAsync<bool>(async module =>
        {
            await call(module);
            return true;
        });

    private async Task<T?> QuietlyAsync<T>(Func<IJSObjectReference, ValueTask<T>> call)
    {
        try
        {
            return await call(await ModuleAsync());
        }
        catch (Exception exception) when (IsDisconnect(exception))
        {
            return default;
        }
    }

    /// <summary>The three ways a circuit that has gone reports itself; see the remarks.</summary>
    private static bool IsDisconnect(Exception exception)
        => exception is JSDisconnectedException or ObjectDisposedException or OperationCanceledException;

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
