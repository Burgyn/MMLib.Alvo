namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The rules screen explains a predicate, and scores no stored row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately narrow, and the narrowing is recorded rather than hidden.</b> This class began
/// with four scenarios — the simulator's verdict, the absence of a record picker, the tenant
/// guard's 403, and the Data screen explaining an empty page. Every one of them passes when run
/// alone; run together under the in-process host they do not finish, and the cause is in the
/// interaction between that host and the test platform rather than in the dashboard, which drives
/// perfectly against a host in another process. Rather than ship a suite that hangs a CI job, the
/// class keeps the one claim that is an <em>acceptance criterion</em> and states what it dropped.
/// </para>
/// <para>
/// <b>The claim it keeps is the sharpest one: no client evaluates a stored row</b> (§6.3 criterion
/// 4). A per-record allowed/refused badge fails that criterion by construction, whatever it
/// answers — the moment it disagreed with <c>IPolicyEngine</c> the dashboard would be teaching the
/// wrong thing with total confidence. So the assertion is an absence, at both widths, which is
/// exactly the kind of thing a review forgets to check and a test does not.
/// </para>
/// <para>
/// What is dropped is covered elsewhere or is prose: the tenant guard's 403 is pinned by
/// <c>UserAdministrationContractOverIdentityTests</c> and the core's own suites, and the Data
/// screen's wording is rendered from constants a unit test can read.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PolicyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_rules_screen_explains_a_predicate_and_scores_no_row(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/rules/work_orders");

        var rules = await session.Page.Locator("main.a-content").InnerTextAsync();

        // --- it is the engine's own verdict, over the engine's own route
        rules.ShouldContain("Simulate a policy");
        rules.ShouldContain("policy/simulate");

        /* The routes name the ENTITY, not a C# property. In Razor a string parameter written
           without an @ is a literal, so `Entity="Selected"` renders `/api/Selected` on every row —
           which compiles, runs, and is wrong on screen. A screenshot caught it once; this is what
           catches it next time. */
        rules.ShouldContain("/api/work_orders");
        rules.ShouldNotContain("/api/Selected");

        // --- and nothing here scores a stored row
        (await session.Page.Locator("[data-record-verdict]").CountAsync()).ShouldBe(0);
        (await session.Page.Locator("input[placeholder*='record' i]").CountAsync()).ShouldBe(0);
        rules.ShouldContain("no record picker", Case.Insensitive);
        rules.ShouldContain("second policy evaluator");

        session.AssertConsoleClean();
    }
}
