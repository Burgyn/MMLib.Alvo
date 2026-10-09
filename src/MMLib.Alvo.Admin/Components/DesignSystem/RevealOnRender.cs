using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using System.Text;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Scrolls an element into view once, when this is first drawn beside it: a created item appears in place and is
/// scrolled to (spec §3.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A component rather than an override on the screen</b>, for <c>FocusOnRender</c>'s reason: every Razor
/// component is public, so an <c>OnAfterRenderAsync</c> written on a tab would be a member of the package's
/// contract. This is internal, and a tab gains one line of markup (<see cref="On"/>).
/// </para>
/// <para>
/// <b>Drawn after the list</b>, so its first after-render runs once the row it names is in the document. Keyed by
/// the attempt, so every new item draws a new one and is scrolled to again.
/// </para>
/// <para>
/// <b>An id built from a declared name goes through <see cref="ById"/></b> (final review M5): an endpoint's or a
/// template's name reaches the copy unchecked through Import, and a name such as <c>a"b</c> made <c>#endpoint-a"b</c> a
/// selector the browser refuses. The script's error then escaped this after-render and ended the circuit. A refused
/// selector is also caught here, so a scroll never costs more than the scroll.
/// </para>
/// </remarks>
internal sealed partial class RevealOnRender : ComponentBase
{
    /// <summary>The CSS selector of the element to bring into view.</summary>
    [Parameter, EditorRequired]
    public string Selector { get; set; } = string.Empty;

    [Inject]
    private IScrollManager Scroll { get; set; } = default!;

    [Inject]
    private ILogger<RevealOnRender> Logger { get; set; } = default!;

    /// <summary>
    /// The markup for one, written <c>@RevealOnRender.On(selector, attempt)</c>: the Razor compiler only discovers
    /// public components as tags, and this one is internal on purpose.
    /// </summary>
    /// <param name="selector">The element to bring into view.</param>
    /// <param name="attempt">A count the owner raises per new item, so each one is revealed.</param>
    public static RenderFragment On(string selector, int attempt) => builder =>
    {
        builder.OpenComponent<RevealOnRender>(0);
        builder.SetKey($"{attempt}:{selector}");
        builder.AddComponentParameter(1, nameof(Selector), selector);
        builder.CloseComponent();
    };

    /// <summary>
    /// The markup for one that brings the element with id <paramref name="id"/> into view, whatever characters the id
    /// holds: it is matched as a quoted attribute value, never spliced into a selector as it is.
    /// </summary>
    /// <param name="id">The element's id.</param>
    /// <param name="attempt">A count the owner raises per new item, so each one is revealed.</param>
    public static RenderFragment ById(string id, int attempt) => On(IdSelector(id), attempt);

    /// <summary>A selector that matches exactly the element whose id is <paramref name="id"/>.</summary>
    /// <remarks>
    /// A CSS string escapes its backslash and its quote with a backslash, and a line break or any other control character
    /// as a hexadecimal escape ending in a space (CSS Syntax §4.3.5); everything else stands for itself.
    /// </remarks>
    /// <param name="id">The id.</param>
    internal static string IdSelector(string id)
    {
        var quoted = new StringBuilder("[id=\"", id.Length + 8);
        foreach (var character in id)
        {
            AppendEscaped(quoted, character);
        }

        return quoted.Append("\"]").ToString();
    }

    private static void AppendEscaped(StringBuilder quoted, char character)
    {
        if (character is '"' or '\\')
        {
            quoted.Append('\\').Append(character);
        }
        else if (char.IsControl(character))
        {
            quoted.Append('\\').Append(((int)character).ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }
        else
        {
            quoted.Append(character);
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await Scroll.ScrollIntoViewAsync(Selector, ScrollBehavior.Auto);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
        {
            /* The circuit went with the screen; there is nothing left to scroll. */
        }
        catch (JSException exception)
        {
            ScrollRefused(Logger, Selector, exception);
        }
    }

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "The browser refused to scroll to {Selector}; the row is drawn, only not scrolled to.")]
    private static partial void ScrollRefused(ILogger logger, string selector, Exception exception);
}
