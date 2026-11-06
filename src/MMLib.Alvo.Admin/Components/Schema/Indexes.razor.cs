using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The Indexes tab: what an entity declares, and the form that adds one — <i>Only where</i> checked live.</summary>
public partial class Indexes : IDisposable
{
    /// <summary>
    /// The working copy the typed filter is checked against, on a clone — cascaded by the entity screen, so its model
    /// stays internal.
    /// </summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    /// <summary>Redraws the box whenever its check has something new to show.</summary>
    public Indexes() => _check.Changed += Redraw;

    private void Redraw() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        _check.Changed -= Redraw;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The box is described by its hint, and by the sentence under it while it has one.</summary>
    private string DescribedBy => _check.DescribedBy(WhereBox) is { } check ? $"{WhereBox}-hint {check}" : $"{WhereBox}-hint";

    private void TypeWhere(string? text)
    {
        _draft.Where = text ?? string.Empty;
        _ = CheckAsync();
    }

    /// <summary>Turns <i>Exclude overlaps</i> on or off; the filter is asked again, since the candidate it sits in changed.</summary>
    private void ChooseExcludes(bool on)
    {
        _refusal.Clear();
        _draft.Excludes = on;
        _ = CheckAsync();
    }

    /// <summary>Picks where the range starts, and forgets an end of another type.</summary>
    private void ChooseFrom(IReadOnlyList<string> picked)
    {
        _draft.From = picked[0];
        if (!_draft.EndFields(Entity).Contains(_draft.To, StringComparer.Ordinal))
        {
            _draft.To = string.Empty;
        }

        _ = CheckAsync();
    }

    /// <summary>
    /// Runs the check to its end and observes its fault: it is fire-and-forget, and a helper that fails must never be the
    /// reason the form misbehaves.
    /// </summary>
    private async Task CheckAsync()
    {
        try
        {
            await _check.SubmitAsync(WhereBox, _draft.Where, AskAsync);
        }
        catch (Exception ex)
        {
            CheckFailed(Logger, ex);
        }
    }

    /// <summary>The index the form would add, with the typed filter in its slot, or nothing while there is no filter to ask about.</summary>
    private Task<ManagementExpressionVerdict?> AskAsync(string source, CancellationToken ct)
        => !_lifetime.Ended && source.Trim().Length > 0 && _draft.Chosen.Count > 0 && Copy is { } copy
            && ExpressionSlots.ForIndexWhere(copy.Json, Entity.Name, _draft.Candidate(), source) is { } slot
            ? Gateway.CheckExpressionAsync(slot.Json, slot.Path, source, ct)
            : Task.FromResult<ManagementExpressionVerdict?>(null);

    /// <summary>Fixed text and the exception only: the entity name is the caller's string and is never logged raw.</summary>
    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "The expression check on an index's filter failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception);

    /// <summary>What apply would say about the filter, under its box, as its sentences — the field editor's own loop.</summary>
    private RenderFragment Findings => builder =>
    {
        var index = 0;
        foreach (var finding in _check.Findings(WhereBox))
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", ExpressionCheck.ProblemClass(finding));
            builder.AddAttribute(2, "id", index == 0 ? $"{WhereBox}-check" : $"{WhereBox}-check-{index}");
            builder.AddAttribute(3, "role", "status");
            builder.AddAttribute(4, "data-testid", $"check-{WhereBox}");
            builder.AddAttribute(5, "data-severity", finding.Severity);
            builder.AddContent(6, finding.Message);
            AddFix(builder, finding.FixSuggestion);
            builder.CloseElement();
            index++;
        }
    };

    private static void AddFix(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder, string? fix)
    {
        if (fix is null)
        {
            return;
        }

        builder.OpenElement(7, "span");
        builder.AddAttribute(8, "class", "a-field__fix");
        builder.AddContent(9, fix);
        builder.CloseElement();
    }
}
