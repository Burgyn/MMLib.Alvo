using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The end-to-end scenarios find elements by test id or by role and name, and the old selectors only ever
/// get fewer (docs/architecture/admin-dashboard-review.md, F-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a class or a sentence is the wrong handle.</b> A design-system class (<c>.a-*</c>) is styling and
/// <c>:has-text(…)</c> is copy, so a scenario pinned to either breaks on a CSS rename or a reworded sentence that
/// changed nothing it tests — and the review's own plan renames classes as pages migrate, and rewrites copy for
/// the operator. <c>AdminSession</c> states the policy; this holds it.
/// </para>
/// <para>
/// <b>A ratchet, not a ban</b>, because the review migrates a scenario when it is touched rather than all at once.
/// The counts fail when they grow, and fail when they shrink without being lowered here, so a migration cannot
/// leave room for the next scenario to spend.
/// </para>
/// <para>
/// <b>Code only.</b> Comments are stripped before counting — a remark explaining why a scenario once used
/// <c>.a-row</c> is not a selector, and counting it would make the prose the thing a migration has to edit.
/// String literals are kept whole, so a <c>//</c> or <c>/*</c> inside one is not mistaken for a comment.
/// </para>
/// </remarks>
public sealed partial class EndToEndSelectorTests
{
    /* These may only go down. Lower them in the commit that migrates a scenario; never raise them. */
    private const int ClassSelectors = 43;
    private const int HasTextSelectors = 51;

    private static readonly string _scenarios = string.Join('\n',
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Admin.Tests.EndToEnd"), "*Scenarios.cs")
            .Order(StringComparer.Ordinal)
            .Select(file => WithoutComments(File.ReadAllText(file))));

    [Fact]
    public void The_scenarios_name_no_more_design_system_classes_than_they_did()
        => Ratchet(ClassSelector().Count(_scenarios), ClassSelectors, "raw .a-* class selectors");

    [Fact]
    public void The_scenarios_match_no_more_copy_with_has_text_than_they_did()
        => Ratchet(HasTextSelector().Count(_scenarios), HasTextSelectors, ":has-text( selectors");

    /// <summary>The counts see what they are meant to, and nothing the policy prefers, and no comment.</summary>
    [Fact]
    public void The_counts_see_a_class_and_a_has_text_and_not_a_test_id_a_role_or_a_comment()
    {
        const string source = """
            /// <summary>Once used <c>.a-row:has-text(</c>.</summary>
            Page.Locator("main.a-content"); Page.Locator("button:has-text('Plan')"); // not .a-grid:has-text(
            /* `.a-error` being absent, :has-text( */ Page.GetByTestId("sidebar");
            Page.GetByRole(AriaRole.Button, new() { Name = "Plan" }); Page.GotoAsync("http://x/.a-kept");
            """;

        var code = WithoutComments(source);

        ClassSelector().Count(code).ShouldBe(2, "main.a-content, and the class inside a string holding //");
        HasTextSelector().Count(code).ShouldBe(1);
    }

    /// <summary>The C# source less its <c>//</c>, <c>///</c> and <c>/* */</c> comments, its strings kept whole.</summary>
    private static string WithoutComments(string source)
        => CommentOrString().Replace(source, match => match.Value[0] == '/' ? " " : match.Value);

    private static void Ratchet(int count, int baseline, string what)
    {
        count.ShouldBeLessThanOrEqualTo(
            baseline, $"the scenarios grew to {count} {what}; use a data-testid or a role and name instead");
        count.ShouldBe(baseline, $"the scenarios are down to {count} {what}; lower the baseline here to {count}");
    }

    [GeneratedRegex(@"\.a-[a-z]")]
    private static partial Regex ClassSelector();

    [GeneratedRegex(@":has-text\(")]
    private static partial Regex HasTextSelector();

    [GeneratedRegex(@"@""(?:""""|[^""])*""|""(?:\\.|[^""\\\n])*""|//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentOrString();
}
