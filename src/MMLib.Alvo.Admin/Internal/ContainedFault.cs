using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The layout's error boundary, again, around content the layout's own cannot reach: an editor's or a confirm's body,
/// which the dialog provider renders outside the page (final review I5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second boundary.</b> A render exception escapes to the nearest boundary above the component that threw,
/// and a dialog's content sits under <c>MudDialogProvider</c>, above the layout's <c>ErrorBoundary</c>. Without this a
/// render bug in a field editor ended the circuit — the pending count, the working copy's view and the shell went
/// with it — where the page's same bug draws a panel with Reload.
/// </para>
/// <para>
/// <b>It tells its owner when it holds a fault</b>, so an editor cannot submit a form the operator can no longer see
/// and a confirm cannot run an action whose details failed to draw. Internal and drawn through <see cref="Around"/>,
/// the way <see cref="FocusOnRender"/> is, so no public component gains a member for it (spec D5's reason). The base
/// class writes the exception to the server log, so the panel's "in the server log" stays true.
/// </para>
/// </remarks>
internal sealed class ContainedFault : ErrorBoundary
{
    /// <summary>Raised with <see langword="true"/> as a fault is caught and <see langword="false"/> as it is recovered.</summary>
    [Parameter]
    public Action<bool>? FaultChanged { get; set; }

    /// <summary>What to draw for a fault: the caught exception and the way back to the content.</summary>
    [Parameter, EditorRequired]
    public RenderFragment<Caught> Panel { get; set; } = _ => _ => { };

    /// <summary>
    /// The markup for one, written <c>@ContainedFault.Around(ChildContent, Panel, faulted =&gt; …)</c>.
    /// </summary>
    /// <param name="content">What is contained.</param>
    /// <param name="panel">What to draw instead when <paramref name="content"/> throws while rendering.</param>
    /// <param name="faultChanged">Told when a fault is caught and when it is recovered.</param>
    public static RenderFragment Around(
        RenderFragment? content, RenderFragment<Caught> panel, Action<bool>? faultChanged = null) => builder =>
    {
        builder.OpenComponent<ContainedFault>(0);
        builder.AddComponentParameter(1, nameof(ChildContent), content);
        builder.AddComponentParameter(2, nameof(Panel), panel);
        builder.AddComponentParameter(3, nameof(FaultChanged), faultChanged);
        builder.CloseComponent();
    };

    /// <inheritdoc />
    protected override void OnParametersSet()
        => ErrorContent = fault => Panel(new Caught(fault, RecoverAndSay));

    /// <inheritdoc />
    protected override async Task OnErrorAsync(Exception exception)
    {
        await base.OnErrorAsync(exception);
        FaultChanged?.Invoke(true);
    }

    private void RecoverAndSay()
    {
        Recover();
        FaultChanged?.Invoke(false);
    }

    /// <summary>A caught render fault, and the way back to the content that threw it.</summary>
    /// <param name="Fault">The exception, for the <c>ErrorPanel</c> to classify.</param>
    /// <param name="Recover">Draws the content again.</param>
    internal sealed record Caught(Exception Fault, Action Recover);
}
