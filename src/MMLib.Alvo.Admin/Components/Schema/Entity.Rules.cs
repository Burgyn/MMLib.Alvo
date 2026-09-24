using Microsoft.AspNetCore.Components.Routing;
using MMLib.Alvo.Admin.Components.Rules;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The rules typed on this entity and not yet saved, and the question leaving with them asks.</summary>
public partial class Entity
{
    /// <summary>
    /// What was typed into each operation's rule, held by the screen rather than by the Rules tab.
    /// </summary>
    /// <remarks>
    /// Only the open tab's body is drawn, so a draft the tab held would be lost on a move to Fields and back. Held
    /// here, it is still in the box, marked unsaved, when the operator returns (spec §3.1: no save-on-blur, and no
    /// silent loss either).
    /// </remarks>
    private readonly RuleDrafts _ruleDrafts = new();

    private string? _draftsFor;
    private (string Target, bool Replace)? _leavingTo;

    /// <summary>Redraws the screen on every change to a draft, so the leave guard follows each keystroke.</summary>
    /// <remarks>
    /// A keystroke re-renders only the Rules tab. Without this the reload prompt (<c>ConfirmExternalNavigation</c>)
    /// kept the value of the last time the screen drew: off for a rule just typed, on for one Escape had reverted.
    /// </remarks>
    public Entity() => _ruleDrafts.Changed += StateHasChanged;

    /// <summary>Whether a rule on this entity holds text that saving would stage.</summary>
    private bool UnsavedRules => _ruleDrafts.AnyDirty(Declared);

    /// <summary>The rule the working copy declares for one operation, or <see langword="null"/>.</summary>
    private string? Declared(string operation)
        => _working.FirstOrDefault(rule => string.Equals(rule.Key, operation, StringComparison.Ordinal)).Value;

    /// <summary>
    /// A move to another entity forgets this one's drafts: they are typed against its rules, and the question on the
    /// way out has already been answered.
    /// </summary>
    private void ForgetOtherEntitysRules()
    {
        if (!string.Equals(_draftsFor, EntityName, StringComparison.Ordinal))
        {
            _ruleDrafts.Clear();
            _draftsFor = EntityName;
        }
    }

    /// <summary>
    /// Holds a navigation away from this entity while a rule is unsaved, and asks; a move between its tabs passes.
    /// </summary>
    private void GuardRules(LocationChangingContext context)
    {
        if (!UnsavedRules || IsThisEntity(context.TargetLocation))
        {
            return;
        }

        context.PreventNavigation();
        /* A link the router intercepted is a step forward; anything else (Back, Forward, a navigation from code) has
           no step of its own to add, so leaving replaces this entry rather than pushing one after it. */
        _leavingTo = (context.TargetLocation, !context.IsNavigationIntercepted);
    }

    /// <summary>Whether <paramref name="location"/> is this entity's own screen, whatever its query.</summary>
    private bool IsThisEntity(string location)
        => string.Equals(
            Navigation.ToAbsoluteUri(location).GetLeftPart(UriPartial.Path),
            new Uri(Navigation.Uri).GetLeftPart(UriPartial.Path),
            StringComparison.Ordinal);

    /// <summary>The confirm's verb: drops the drafts and goes where the operator was going.</summary>
    private void LeaveDiscardingRules()
    {
        var leaving = _leavingTo;
        _leavingTo = null;
        _ruleDrafts.Clear();

        if (leaving is { } to)
        {
            Navigation.NavigateTo(to.Target, replace: to.Replace);
        }
    }
}
