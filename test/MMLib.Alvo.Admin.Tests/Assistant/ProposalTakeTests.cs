using MMLib.Alvo.Admin.Components.Assistant;
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Assistant;

/// <summary>
/// The assistant's proposal reaches the working copy only when the copy takes it, and a refusal is said rather than
/// followed by a Preview of whatever the copy already held (docs/todo-admin.md §8d item 42).
/// </summary>
public class ProposalTakeTests
{
    private const string Applied = """{"name":"p","entities":{}}""";

    [Fact]
    public void A_proposal_that_is_an_object_is_taken_with_its_reason()
    {
        var copy = Copy();

        ProposalTake.Into(copy, """{"name":"p","entities":{"tickets":{"fields":{}}}}""", "Add tickets").ShouldBeNull();

        copy.Entities.ShouldBe(["tickets"]);
        copy.SuggestedReason.ShouldBe("Add tickets");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{not json")]
    public void A_proposal_the_copy_refuses_is_refused_and_nothing_is_suggested(string proposal)
    {
        var copy = Copy();

        ProposalTake.Into(copy, proposal, "Add tickets").ShouldBe(ProposalTake.NotAnObject);

        copy.IsDirty.ShouldBeFalse();
        copy.SuggestedReason.ShouldBeNull("a reason for edits that never landed would be carried by the next apply");
    }

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Applied, 1);
        return copy;
    }
}
