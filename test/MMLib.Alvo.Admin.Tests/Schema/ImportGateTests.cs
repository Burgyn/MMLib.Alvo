using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Import runs only over a loaded working copy, and only with something pasted (Task 11 re-review).</summary>
public sealed class ImportGateTests
{
    private static readonly ManagementDescriptor _loaded = new("p", 3, "{}");

    [Fact]
    public void A_paste_over_a_loaded_copy_may_be_imported()
        => ImportGate.Ready(_loaded, problem: null, "{}").ShouldBeTrue();

    [Fact]
    public void Nothing_is_imported_before_the_page_has_loaded_the_applied_descriptor()
        => ImportGate.Ready(descriptor: null, problem: null, "{}").ShouldBeFalse();

    [Fact]
    public void Nothing_is_imported_while_the_page_shows_a_problem()
        => ImportGate.Ready(_loaded, new AdminProblem("t", "d", null, new InvalidOperationException(), IsFault: true), "{}")
            .ShouldBeFalse("a copy that failed to load could be replaced over edits it never heard of");

    [Fact]
    public void Loaded_is_the_ready_rule_without_the_box()
    {
        ImportGate.Loaded(_loaded, problem: null).ShouldBeTrue();
        ImportGate.Loaded(descriptor: null, problem: null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void A_blank_box_is_not_imported(string pasted)
        => ImportGate.Ready(_loaded, problem: null, pasted).ShouldBeFalse();
}
