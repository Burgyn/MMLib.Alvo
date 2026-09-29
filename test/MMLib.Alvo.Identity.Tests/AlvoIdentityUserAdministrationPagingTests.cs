using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The administration's list pages by its own cursor, over a real SQLite file: nothing asked for a second page until
/// the Access screen did (docs/todo-admin.md §8d item 23), and the cursor's comparison did not translate to SQL.
/// </summary>
public sealed class AlvoIdentityUserAdministrationPagingTests : IAsyncLifetime
{
    private static readonly string[] _everybody =
        ["ada@alvo.test", "Bob@alvo.test", "cyd@alvo.test", "dot@alvo.test", "eve@alvo.test"];

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    private IAlvoUserAdministration People
        => _scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateScope();
        await _scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>().Database
            .EnsureCreatedAsync(TestContext.Current.CancellationToken);

        /* Created out of order, so the pages prove the address order rather than the insertion order. */
        foreach (var email in _everybody.Reverse())
        {
            await People.CreateAsync(new AlvoUserCreation(email, []), TestContext.Current.CancellationToken);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();
        /* This file's pool only: clearing every pool in the process races the other classes' open connections. */
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
    }

    /// <summary>
    /// Following each page's cursor reaches everybody once, in address order ignoring case, and then stops.
    /// </summary>
    /// <remarks>
    /// "Bob" is capitalised because a raw column orders it before "ada" under SQLite's BINARY collation; the order and
    /// the cursor are the normalised name's, which is unique and indexed, so the two cannot disagree.
    /// </remarks>
    [Fact]
    public async Task The_cursor_walks_every_page_without_skipping_or_repeating_anybody()
    {
        var seen = new List<string>();
        var query = new AlvoUserQuery(Limit: 2);

        for (var pages = 0; pages < 10; pages++)
        {
            var page = await People.ListAsync(query, TestContext.Current.CancellationToken);
            seen.AddRange(page.Users.Select(person => person.Email));
            if (page.NextCursor is not { } next)
            {
                break;
            }

            query = query with { After = next };
        }

        seen.ShouldBe(_everybody);
    }

    /// <summary>A search pages by the same cursor, and never reaches past what it matches (a "d" left of the @).</summary>
    [Fact]
    public async Task A_search_pages_within_what_it_matches()
    {
        var first = await People.ListAsync(new AlvoUserQuery("d", Limit: 2), TestContext.Current.CancellationToken);
        var second = await People.ListAsync(
            new AlvoUserQuery("d", Limit: 2, After: first.NextCursor), TestContext.Current.CancellationToken);

        first.Users.Select(person => person.Email).ShouldBe(["ada@alvo.test", "cyd@alvo.test"]);
        second.Users.Select(person => person.Email).ShouldBe(["dot@alvo.test"]);
        second.NextCursor.ShouldBeNull();
    }

    /// <summary>A search ignores case, as signing in with an address does.</summary>
    [Fact]
    public async Task A_search_ignores_case()
    {
        var page = await People.ListAsync(new AlvoUserQuery("ADA"), TestContext.Current.CancellationToken);

        page.Users.Select(person => person.Email).ShouldBe(["ada@alvo.test"]);
    }

    /// <summary>The total is how many there are, not how many are left after the page on screen.</summary>
    [Fact]
    public async Task The_total_counts_everybody_the_query_matches_on_every_page()
    {
        var first = await People.ListAsync(new AlvoUserQuery(Limit: 2), TestContext.Current.CancellationToken);
        var second = await People.ListAsync(
            new AlvoUserQuery(Limit: 2, After: first.NextCursor), TestContext.Current.CancellationToken);
        var searched = await People.ListAsync(
            new AlvoUserQuery("d", Limit: 2, After: first.NextCursor), TestContext.Current.CancellationToken);

        first.TotalCount.ShouldBe(5);
        second.TotalCount.ShouldBe(5);
        searched.TotalCount.ShouldBe(3);
    }
}
