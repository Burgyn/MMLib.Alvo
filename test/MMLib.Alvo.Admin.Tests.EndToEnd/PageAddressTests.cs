namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The globs the scenarios wait for mean what Playwright's own mean (<see cref="PageAddress"/>).</summary>
/// <remarks>
/// Here rather than in ring0 because <see cref="PageAddress"/> is this project's, and no ring references it: these run only
/// in the admin e2e job, which is the only place the glob is used.
/// </remarks>
public sealed class PageAddressTests
{
    [Theory]
    [InlineData("**/changes", "http://127.0.0.1:5000/admin/changes", true)]
    [InlineData("**/changes", "http://127.0.0.1:5000/admin/changes?x=1", false)]
    [InlineData("**/schema/gauges", "http://127.0.0.1:5000/admin/schema/gauges", true)]
    [InlineData("**layers=reactions**", "http://h/admin/schema?view=map&layers=reactions&zoom=actual", true)]
    [InlineData("**/admin/sign-in?**", "http://h/admin/sign-in?returnUrl=%2Fadmin", true)]
    [InlineData("**/admin/sign-in?**", "http://h/admin/sign-inX", false)]
    [InlineData("**/admin/*", "http://h/admin/schema", true)]
    [InlineData("**/admin/*", "http://h/admin/schema/regions", false)]
    public void A_glob_matches_the_whole_address(string glob, string url, bool matches)
        => PageAddress.Pattern(glob).IsMatch(url).ShouldBe(matches);
}
