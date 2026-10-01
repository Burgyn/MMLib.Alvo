using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// The error panel prints what the screen classified, and classifies only what nothing upstream could.
/// </summary>
public class ErrorPanelTextTests
{
    [Fact]
    public void A_record_write_validation_refusal_keeps_its_own_sentence()
    {
        var refusal = new ArgumentException("values omits a required field: code.");
        var classified = AdminProblem.From(refusal, ProblemSite.RecordWrite)!;

        var text = ErrorPanelText.Of(classified, fault: null, title: null, fix: null);

        text.Headline.ShouldBe("The values were refused");
        text.Detail.ShouldBe("values omits a required field: code.");
    }

    [Fact]
    public void A_people_refusal_for_an_unsupported_operation_is_not_turned_into_a_fault()
    {
        var classified = AdminProblem.From(new NotSupportedException("This store cannot rename people."), ProblemSite.People)!;

        var text = ErrorPanelText.Of(classified, fault: null, title: null, fix: null);

        text.Headline.ShouldNotBe(AdminProblem.FaultTitle);
        text.Detail.ShouldBe("This store cannot rename people.");
    }

    [Fact]
    public void A_raw_fault_is_classified_without_a_site_and_never_prints_its_message()
    {
        var text = ErrorPanelText.Of(null, new InvalidOperationException("secret detail"), title: null, fix: null);

        text.Headline.ShouldBe(AdminProblem.FaultTitle);
        text.Detail.ShouldBe(AdminProblem.FaultDetail);
    }

    [Fact]
    public void A_raw_fault_wins_over_a_cascaded_problem_because_it_is_the_nearer_input()
    {
        var cascaded = AdminProblem.From(new ManagementRequestException("outer"))!;

        var text = ErrorPanelText.Of(cascaded, new InvalidOperationException("inner"), title: null, fix: null);

        text.Detail.ShouldBe(AdminProblem.FaultDetail);
    }

    [Fact]
    public void The_callers_title_and_fix_win_over_the_classification()
    {
        var classified = AdminProblem.From(new ManagementForbiddenException())!;

        var text = ErrorPanelText.Of(classified, fault: null, title: "Not here", fix: "Ask an owner.");

        text.Headline.ShouldBe("Not here");
        text.Fix.ShouldBe("Ask an owner.");
        text.OffersSignOut.ShouldBeTrue();
    }

    [Fact]
    public void A_panel_given_only_a_title_prints_it_over_an_empty_detail()
    {
        var text = ErrorPanelText.Of(null, null, "That name cannot be used", "Pick another.");

        text.Headline.ShouldBe("That name cannot be used");
        text.Detail.ShouldBeEmpty();
        text.Fix.ShouldBe("Pick another.");
        text.OffersSignOut.ShouldBeFalse();
    }
}
