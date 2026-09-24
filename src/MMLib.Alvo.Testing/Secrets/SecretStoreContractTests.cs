using MMLib.Alvo.Secrets;
using Shouldly;

namespace MMLib.Alvo.Testing.Secrets;

/// <summary>
/// Behavioural contract every <see cref="ISecretStore"/> must satisfy — fake, configuration-backed and
/// database-backed alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interface-first, so this exists before either implementation does</b> (§0 principle 1). A store is the
/// one port where a plausible implementation can be wrong in a way nothing notices: a value that comes back
/// trimmed, an unknown name that throws, a listing that carries values. Each of those is a fact here rather
/// than a habit.
/// </para>
/// <para>
/// <b>A read-only store runs the same suite.</b> The facts that need a write say so through
/// <see cref="SupportsWriting"/> rather than being split into a second class — the alternative is two suites
/// that drift, and the half a configuration-backed store still owes is most of this one.
/// </para>
/// </remarks>
public abstract class SecretStoreContractTests
{
    /// <summary>Creates the store under test, empty.</summary>
    protected abstract ISecretStore CreateStore();

    /// <summary>Whether this store accepts writes; <see langword="false"/> for a read-only one.</summary>
    protected virtual bool SupportsWriting => true;

    /// <summary>A value written is the value read back, byte for byte.</summary>
    /// <remarks>
    /// The spaces are deliberate. A store that trimmed would pass every test written with a tidy value and
    /// then hand a caller a key that authenticates against nothing.
    /// </remarks>
    [Fact]
    public async Task Get_returns_what_Set_wrote()
    {
        if (!SupportsWriting)
        {
            return;
        }

        var store = CreateStore();
        await store.SetAsync(Name("one.two"), "  a value with spaces  ");

        (await store.GetAsync(Name("one.two"))).ShouldBe("  a value with spaces  ");
    }

    /// <summary>An unknown name is absence, not an error.</summary>
    /// <remarks>
    /// Asking whether a secret exists is an ordinary question — the AI connection asks it on every turn — and
    /// a store that threw would make every caller wrap it.
    /// </remarks>
    [Fact]
    public async Task Unknown_name_is_null_rather_than_an_exception() =>
        (await CreateStore().GetAsync(Name("never.written"))).ShouldBeNull();

    /// <summary>A second write replaces the first: a store is a map, not a history.</summary>
    [Fact]
    public async Task Set_replaces_an_existing_value()
    {
        if (!SupportsWriting)
        {
            return;
        }

        var store = CreateStore();
        await store.SetAsync(Name("k"), "first");
        await store.SetAsync(Name("k"), "second");

        (await store.GetAsync(Name("k"))).ShouldBe("second");
    }

    /// <summary><see cref="ISecretStore.DeleteAsync"/> reports whether it removed anything.</summary>
    [Fact]
    public async Task Delete_reports_whether_it_removed_anything()
    {
        if (!SupportsWriting)
        {
            return;
        }

        var store = CreateStore();
        await store.SetAsync(Name("k"), "v");

        (await store.DeleteAsync(Name("k"))).ShouldBeTrue();
        (await store.DeleteAsync(Name("k"))).ShouldBeFalse();
        (await store.GetAsync(Name("k"))).ShouldBeNull();
    }

    /// <summary>A listing carries names and no values.</summary>
    /// <remarks>
    /// A settings screen needs the names; the one thing that would need every value at once is an
    /// exfiltration. Asserted by looking for the value in what the listing renders, because a
    /// <see cref="SecretName"/> that carried its value would satisfy a count.
    /// </remarks>
    [Fact]
    public async Task ListNames_returns_names_and_no_values()
    {
        if (!SupportsWriting)
        {
            return;
        }

        var store = CreateStore();
        await store.SetAsync(Name("a.one"), "secret-value");
        await store.SetAsync(Name("b.two"), "another");

        var names = await store.ListNamesAsync();

        names.Select(name => name.Value).OrderBy(value => value, StringComparer.Ordinal)
            .ShouldBe(["a.one", "b.two"]);
        string.Join(" ", names.Select(name => name.ToString())).ShouldNotContain("secret-value");
    }

    /// <summary>A store that cannot write says so, and refuses rather than accepting silently.</summary>
    [Fact]
    public async Task A_store_that_cannot_write_says_so_and_refuses()
    {
        var store = CreateStore();
        if (store.CanWrite)
        {
            return;
        }

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await store.SetAsync(Name("k"), "v"));
    }

    /// <summary><see cref="ISecretStore.CanWrite"/> answers what the store actually does.</summary>
    /// <remarks>
    /// The control the fact above needs: a store whose <c>CanWrite</c> disagreed with its behaviour would
    /// satisfy either half alone, and the screen that asks it before offering a control would be lying in one
    /// direction or the other.
    /// </remarks>
    [Fact]
    public async Task CanWrite_agrees_with_what_a_write_does()
    {
        var store = CreateStore();

        if (store.CanWrite)
        {
            await store.SetAsync(Name("probe"), "v");
            (await store.GetAsync(Name("probe"))).ShouldBe("v");
            return;
        }

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await store.SetAsync(Name("probe"), "v"));
    }

    private static SecretName Name(string value) => SecretName.Parse(value);
}
