using MMLib.Alvo.Migrations;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The second boot over a database the first one created — the path an operator takes on <em>every</em>
/// restart, and the one no other fact in this repository exercises.
/// </summary>
/// <remarks>
/// <para>
/// Every other host fact mints a fresh SQLite file, and <c>scripts/test-e2e</c> runs
/// <c>docker compose down --volumes</c> before every run; both are right for their own purposes and both
/// mean the migration plan on the only boot they see is a create. A restart's plan is a <em>diff</em>, and
/// the two outcomes that diff can have — nothing to do, and something refused — are opposite requirements
/// on the same predicate. Hence two facts over one shared database file rather than one.
/// </para>
/// <para>
/// They are a pair on purpose. Guarding a refusal with a bare <c>!Applied</c> would also reject the empty
/// plan, which is what an unchanged descriptor produces on every ordinary restart: the *more* common case,
/// and a worse outage than the one the guard exists to prevent.
/// </para>
/// </remarks>
public class AlvoHostRestartTests
{
    private const string DroppedFieldDescriptor = "host-boot-dropped-field.alvo.json";

    /// <summary>
    /// An unchanged descriptor restarts: the plan is empty, the host still maps its routes, and the rows the
    /// first boot wrote are still there.
    /// </summary>
    /// <remarks>
    /// The read-back is what makes this a restart rather than two unrelated hosts — a second boot over a
    /// <em>different</em> database would map the same routes and answer an empty list.
    /// </remarks>
    [Fact]
    public async Task An_unchanged_descriptor_restarts_over_the_database_the_first_boot_created()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await CreateWarehouseAsync(databasePath);

            await using var restarted = await AlvoHostWorld.StartAsync(databasePath: databasePath);

            using var listed = await restarted.GetAsync("/api/warehouses");

            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldContain("W-1");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// A descriptor that drops a field fails the restart, and says which step it refused.
    /// </summary>
    /// <remarks>
    /// The message assertion is the fact, not the throw. A start that failed for an unrelated reason — a
    /// descriptor the fixture mistyped, a missing file — satisfies "it threw" just as well, and this fact
    /// would then pass while proving nothing about the refusal. Data is safe either way; what this pins is
    /// that availability is not silently zero instead.
    /// </remarks>
    [Fact]
    public async Task A_descriptor_that_drops_a_field_fails_the_restart_and_names_the_step()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await CreateWarehouseAsync(databasePath);

            var failure = await Should.ThrowAsync<DestructiveChangeNotAllowedException>(
                () => AlvoHostWorld.StartAsync(DroppedFieldDescriptor, overrides: null, databasePath: databasePath));

            failure.Message.ShouldContain("DropField");
            failure.Message.ShouldContain("warehouses.city");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// A refused start disposes the application it had already built, so nothing keeps the database file open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>builder.Build()</c> creates a full service provider before anything can fail, and the apply is what
    /// fails; a version of <see cref="AlvoHost.BuildAsync"/> that let the exception out without disposing left
    /// the store's connection pool holding this file for the rest of the process. In a container it is a
    /// process holding a socket and a file handle while the orchestrator restarts it.
    /// </para>
    /// <para>
    /// The claim is asserted on the <em>container</em>, not on <c>File.Delete</c>, because deleting an open
    /// file succeeds on Unix and fails only on Windows — a fact written the other way round would measure the
    /// leak on one CI runner and nothing at all on a developer's machine. The delete still happens, in the
    /// <c>finally</c> every fact here shares, and <c>TryDeleteDatabase</c> no longer swallows its failure.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_refused_restart_disposes_the_application_it_had_already_built()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        DisposalProbe? probe = null;

        try
        {
            await CreateWarehouseAsync(databasePath);

            await Should.ThrowAsync<DestructiveChangeNotAllowedException>(
                () => AlvoHostWorld.StartAsync(
                    DroppedFieldDescriptor,
                    overrides: null,
                    databasePath: databasePath,
                    configure: builder => probe = DisposalProbe.RegisteredOn(builder)));

            probe.ShouldNotBeNull("the fixture must really have registered the probe");
            probe.Disposed.ShouldBeTrue(
                "a refused start must dispose the application it built, or the connection pool keeps the "
                + "database file open for the rest of the process");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>Boots once over <paramref name="databasePath"/>, writes one row, and stops.</summary>
    private static async Task CreateWarehouseAsync(string databasePath)
    {
        await using var first = await AlvoHostWorld.StartAsync(databasePath: databasePath);

        using var created = await first.SendAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-1", ["city"] = "Košice" });

        created.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "the first boot must really create the schema and a row, or the restart below diffs against nothing");
    }
}
