using Microsoft.JSInterop;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The .NET end of one <c>alvo:*</c> document event, for a component that must not grow a public method.
/// </summary>
/// <remarks>
/// Blazor calls only a public <see cref="JSInvokableAttribute"/> method, and every Razor component is
/// public — so a callback written on the component is a member of the package's contract. The method
/// is public here instead, on a type nobody outside the dashboard can name.
/// </remarks>
/// <param name="handler">What the event does, with the value alvo.js sent along with it.</param>
internal sealed class JsEventHandler(Func<string?, Task> handler)
{
    /// <summary>The name admin.js invokes.</summary>
    public const string Method = nameof(Invoke);

    /// <summary>Called by admin.js when the event fires.</summary>
    [JSInvokable]
    public Task Invoke(string? value) => handler(value);
}
