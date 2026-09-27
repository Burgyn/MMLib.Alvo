using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>
/// The people list asks the port's own <c>Search</c> and <c>After</c> — it used to ask for the first 50 and stop
/// (docs/todo-admin.md §8d item 23; <c>IAlvoUserAdministration.cs:125</c>).
/// </summary>
public class PeoplePagingTests
{
    [Fact]
    public void The_first_page_asks_for_everyone_from_the_start()
        => new PeoplePaging().Query.ShouldBe(new AlvoUserQuery(Search: null, Limit: 50, After: null));

    [Fact]
    public void A_search_is_trimmed_and_starts_from_the_first_page()
    {
        var paging = new PeoplePaging();
        paging.Next("c1");

        paging.Find("  dispatcher ");

        paging.Query.ShouldBe(new AlvoUserQuery("dispatcher", 50, After: null));
        paging.HasEarlier.ShouldBeFalse();
    }

    [Fact]
    public void Next_and_previous_walk_the_cursors_back_to_the_start()
    {
        var paging = new PeoplePaging();

        paging.Next("c1");
        paging.Next("c2");
        paging.Query.After.ShouldBe("c2");

        paging.Previous();
        paging.Query.After.ShouldBe("c1");
        paging.Previous();
        paging.Query.After.ShouldBeNull();
        paging.HasEarlier.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_search_is_no_search()
    {
        var paging = new PeoplePaging();
        paging.Find("   ");

        paging.Query.Search.ShouldBeNull();
    }
}
