using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>The markup of a <see cref="RefusalState{T}"/>: the titled <see cref="ErrorPanel"/>, keyed per attempt.</summary>
internal static class RefusalPanel
{
    /// <summary>A refusal the screen wrote as a sentence: the panel under <paramref name="title"/>, with it as the fix.</summary>
    /// <param name="refusal">The state.</param>
    /// <param name="title">What cannot be done: "That index cannot be added".</param>
    /// <param name="slug">The problem-type slug, when one applies.</param>
    /// <returns>The markup, or nothing while no refusal is shown.</returns>
    public static RenderFragment Of(RefusalState<string> refusal, string title, string? slug = null) => builder =>
    {
        if (refusal.Current is not { } fix)
        {
            return;
        }

        builder.OpenComponent<ErrorPanel>(0);
        builder.SetKey(refusal.Key);
        builder.AddComponentParameter(1, nameof(ErrorPanel.Title), refusal.Title ?? title);
        builder.AddComponentParameter(2, nameof(ErrorPanel.Fix), fix);
        builder.AddComponentParameter(3, nameof(ErrorPanel.Slug), slug);
        builder.AddComponentParameter(4, nameof(ErrorPanel.TakeFocus), refusal.TakesFocus);
        builder.CloseComponent();
    };

    /// <summary>A refusal the screen classified: the panel over its classification, with the screen's actions.</summary>
    /// <param name="refusal">The state.</param>
    /// <param name="actions">What the operator can do from here, such as Reload.</param>
    /// <returns>The markup, or nothing while no refusal is shown.</returns>
    public static RenderFragment Of(RefusalState<AdminProblem> refusal, RenderFragment? actions = null) => builder =>
    {
        if (refusal.Current is not { } problem)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<AdminProblem>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<AdminProblem>.Value), problem);
        builder.AddComponentParameter(2, nameof(CascadingValue<AdminProblem>.ChildContent), Classified(refusal, actions));
        builder.CloseComponent();
    };

    private static RenderFragment Classified(RefusalState<AdminProblem> refusal, RenderFragment? actions) => builder =>
    {
        builder.OpenComponent<ErrorPanel>(0);
        builder.SetKey(refusal.Key);
        builder.AddComponentParameter(1, nameof(ErrorPanel.Title), refusal.Title);
        builder.AddComponentParameter(2, nameof(ErrorPanel.TakeFocus), refusal.TakesFocus);
        builder.AddComponentParameter(3, nameof(ErrorPanel.Actions), actions);
        builder.CloseComponent();
    };
}
