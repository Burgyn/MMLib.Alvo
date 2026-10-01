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

    private static readonly AlvoUser[] _one = [Person("ada@alvo.test")];

    [Fact]
    public void A_created_person_is_revealed_by_their_address_and_a_search_typed_after_it_is_the_operators_own()
    {
        var paging = new PeoplePaging();
        paging.Next("c1");

        paging.Reveal("zz@alvo.test");

        paging.Query.ShouldBe(new AlvoUserQuery("zz@alvo.test", 50, After: null));
        paging.Revealing.ShouldBeTrue();
        paging.HasEarlier.ShouldBeFalse();

        paging.Find("zz");
        paging.Revealing.ShouldBeFalse();
    }

    [Fact]
    public void Typing_ends_a_reveal_before_the_search_it_types_runs()
    {
        var paging = new PeoplePaging();
        paging.Reveal("zz@alvo.test");

        paging.StopRevealing();

        paging.Revealing.ShouldBeFalse();
        paging.Query.Search.ShouldBe("zz@alvo.test", "the list stays as it is until the typed search runs");
    }

    [Fact]
    public void Only_an_unsearched_single_page_with_room_on_it_shows_everybody()
    {
        new PeoplePaging().ShowsEverybody(new AlvoUserPage(_one)).ShouldBeTrue();

        new PeoplePaging().ShowsEverybody(new AlvoUserPage(_one, NextCursor: "c1")).ShouldBeFalse("there is a next page");
        new PeoplePaging().ShowsEverybody(new AlvoUserPage([.. Enumerable.Repeat(_one[0], 50)]))
            .ShouldBeFalse("a full page: one more person would start the next page");

        var searched = new PeoplePaging();
        searched.Find("ada");
        searched.ShowsEverybody(new AlvoUserPage(_one)).ShouldBeFalse("a search hides whoever it does not match");

        var later = new PeoplePaging();
        later.Next("c1");
        later.ShowsEverybody(new AlvoUserPage(_one)).ShouldBeFalse("there is a page before this one");
    }

    private static AlvoUser Person(string email) => new() { Id = UserId.New(), Email = email, RoleNames = [] };
}
