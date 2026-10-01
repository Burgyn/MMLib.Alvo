using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// What an <see cref="ErrorPanel"/> prints, worked out from whatever it was given.
/// </summary>
/// <remarks>
/// <b>A problem the screen classified is printed as classified.</b> Only the screen knows where it caught
/// an exception, and the site decides whether an <see cref="ArgumentException"/> is the data port's
/// validation refusal or a fault; classifying it a second time here, without the site, turned that refusal
/// into "Something went wrong" over a log line that was never written. A raw exception is classified here
/// only because nothing upstream could: it is what an <c>ErrorBoundary</c> caught while rendering.
/// </remarks>
/// <param name="Headline">The panel's title.</param>
/// <param name="Detail">The sentence under it.</param>
/// <param name="Fix">What to do about it, when anything is known.</param>
/// <param name="OffersSignOut">Whether the panel offers to sign out and in again.</param>
internal sealed record ErrorPanelText(string Headline, string Detail, string? Fix, bool OffersSignOut)
{
    /// <summary>Projects the panel's inputs onto what it prints.</summary>
    /// <param name="classified">The screen's own classification, cascaded to the panel.</param>
    /// <param name="fault">An exception nothing classified; it wins over the cascade, being the nearer input.</param>
    /// <param name="title">A headline the caller prefers to the classification's.</param>
    /// <param name="fix">A fix the caller prefers to the classification's.</param>
    public static ErrorPanelText Of(AdminProblem? classified, Exception? fault, string? title, string? fix)
    {
        var problem = fault is null ? classified : AdminProblem.From(fault) ?? AdminProblem.Fault(fault);

        return new ErrorPanelText(
            title ?? problem?.Title ?? AdminProblem.FaultTitle,
            problem?.Detail ?? string.Empty,
            fix ?? problem?.Fix,
            problem?.OffersSignOut == true);
    }
}
