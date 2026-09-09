using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// The four behavioural claims that must hold for every descriptor, not only for the demo.
/// </summary>
/// <remarks>
/// <para>
/// Written as methods over <c>(world, project)</c> rather than as tests, because
/// <c>SabotageTests</c> runs the very same bodies against a deliberately broken host and asserts they go
/// <b>red</b>. An invariant that only ever ran green would prove nothing about the code it watches, and
/// #26's definition of done names this explicitly.
/// </para>
/// <para>
/// Every loop asserts how many routes or rows it actually reached, so a typo in a path cannot make a claim
/// pass by testing nothing — the same "pin the count from outside" discipline the document facts use.
/// </para>
/// </remarks>
internal static class BehaviourInvariants
{
    /// <summary>The ten routes an entity gets, as (method, path suffix) pairs.</summary>
    /// <remarks>
    /// A literal rather than a walk of the endpoint table: this is the set default-deny has to cover, and
    /// taking it from the same source the code builds routes from would make the claim circular.
    /// </remarks>
    private static readonly (HttpMethod Method, string Suffix, bool NeedsBody)[] _routes =
    [
        (HttpMethod.Get, "", false),
        (HttpMethod.Post, "", true),
        (HttpMethod.Post, "/query", true),
        (HttpMethod.Get, "/{id}", false),
        (HttpMethod.Patch, "/{id}", true),
        (HttpMethod.Put, "/{id}", true),
        (HttpMethod.Delete, "/{id}", false),
        (HttpMethod.Post, "/batch", true),
        (HttpMethod.Patch, "/batch", true),
        (HttpMethod.Delete, "/batch", true),
    ];

    /// <summary>
    /// An entity the descriptor configures no rule for is refused on every route, and no statement is composed.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <b>The key carries every scope</b> (<c>*:read</c>, <c>*:write</c>), so a 403 here cannot be about the
    /// credential — it can only be the missing rule, which is what default-deny means. And the refusal has to
    /// happen <em>before</em> the data port composes a statement: a policy that denied after reaching storage
    /// would still answer 403 while having touched rows, so the SQL recorder is what separates "refused" from
    /// "refused in time".
    /// </remarks>
    internal static async Task DefaultDenyAsync(AlvoApiWorld world, GeneratedProject project)
    {
        // Or the count below is satisfied by 0 == 0 and this claim walks nothing. It is pinned in
        // GeneratedProjectTests too; pinned here as well because this method is what the sabotage battery
        // asserts can fail, and a vacuous version of it could not.
        project.DeniedEntities.ShouldNotBeEmpty("every generated project must declare an unconfigured entity");

        var reached = 0;
        foreach (var entity in project.DeniedEntities)
        {
            foreach (var (method, suffix, needsBody) in _routes)
            {
                world.ClearStatements();
                var path = $"/api/{entity}{suffix.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)}";
                using var response = await world.SendAsync(
                    method, path, project.Admin(), body: needsBody ? Batch(method, suffix) : null);

                response.StatusCode.ShouldBe(
                    HttpStatusCode.Forbidden,
                    $"{method} {path} must be refused: '{entity}' configures no rule for anything");
                world.Statements.ShouldBeEmpty(
                    $"{method} {path} refused only after composing a statement, which is a refusal too late");
                reached++;
            }
        }

        reached.ShouldBe(
            project.DeniedEntities.Count * _routes.Length,
            "or this claim did not reach every route of every unconfigured entity");
    }

    /// <summary>
    /// Every route that reads a body refuses one that is not declared as JSON — the cross-descriptor half
    /// of #191.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <para>
    /// <b>Over the <em>permissive</em> entities, not the denied ones.</b> A denied entity is refused by the
    /// decision before the delegate interprets a single header, so running this there would assert 415 and
    /// get 403 — for a reason that is not the guard.
    /// </para>
    /// <para>
    /// <b>And it is the shape a metadata-driven framework actually gets wrong.</b> The ring0 facts prove
    /// the guard over one descriptor; this proves it over the whole corpus, which is where "works for the
    /// demo, breaks on another combination of fields" lives. The body is valid JSON in every request: what
    /// is being measured is the declaration, and a body that would also have failed validation could pass
    /// this while the guard did nothing.
    /// </para>
    /// </remarks>
    internal static async Task NonJsonBodiesAreRefusedAsync(AlvoApiWorld world, GeneratedProject project)
    {
        project.PermissiveEntities.ShouldNotBeEmpty(
            "every generated project must declare an entity that admits its admin, or this walks nothing");

        var bodyTaking = _routes.Where(route => route.NeedsBody).ToList();
        var reached = 0;
        foreach (var entity in project.PermissiveEntities)
        {
            foreach (var (method, suffix, _) in bodyTaking)
            {
                var path = $"/api/{entity}{suffix.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)}";
                using var content = new StringContent(
                    Batch(method, suffix).ToJsonString(), Encoding.UTF8, "text/plain");
                using var response = await world.SendRawAsync(method, path, project.Admin(), content: content);

                response.StatusCode.ShouldBe(
                    HttpStatusCode.UnsupportedMediaType,
                    $"{method} {path} reads a body, so a text/plain declaration must be refused");
                reached++;
            }
        }

        reached.ShouldBe(
            project.PermissiveEntities.Count * bodyTaking.Count,
            "or this claim did not reach every body-taking route of every permissive entity");
    }

    /// <summary>
    /// An entity with rules for <em>some</em> operations is refused on exactly the others.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <para>
    /// <b>This is the half of default-deny that "no rules at all" does not reach.</b> §0.5 is a per-operation
    /// guarantee — a rule for <c>list</c> does not admit a <c>delete</c> — and it is the shape a real
    /// descriptor has, where an author grants reads and forgets writes. An all-or-nothing corpus proves the
    /// easy direction only.
    /// </para>
    /// <para>
    /// Asserted over the five unambiguous single-row routes. A configured operation is asserted <b>not</b>
    /// 403 rather than asserted successful: a create may still answer 422 for a payload this method does not
    /// build carefully, and what is under test is reachability, not the write.
    /// </para>
    /// </remarks>
    internal static async Task PerOperationDefaultDenyAsync(AlvoApiWorld world, GeneratedProject project)
    {
        project.PartialEntities.ShouldNotBeEmpty("every generated project must declare a partially-ruled entity");

        var reached = 0;
        foreach (var (entity, configured) in project.PartialEntities)
        {
            configured.Count.ShouldBeInRange(1, 4, $"'{entity}' is not partially configured at all");

            foreach (var (operation, method, suffix) in _singleRowRoutes)
            {
                var path = $"/api/{entity}{suffix.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)}";
                // `forCreate` only on the POST: a create on a tenant-scoped entity has to echo `tenant_id`,
                // and a patch that sent the same member would be refused for naming a column it may not
                // write — a 403 that looks exactly like the per-operation refusal under test.
                using var response = await world.SendAsync(
                    method, path, project.Admin(), body: method == HttpMethod.Get || method == HttpMethod.Delete
                        ? null
                        : Body(project, entity, salt: 500, everyField: false, forCreate: method == HttpMethod.Post));

                if (configured.Contains(operation))
                {
                    response.StatusCode.ShouldNotBe(
                        HttpStatusCode.Forbidden,
                        $"'{entity}' configures '{operation}', so {method} {path} must be reachable");
                }
                else
                {
                    response.StatusCode.ShouldBe(
                        HttpStatusCode.Forbidden,
                        $"'{entity}' configures no '{operation}' rule, so {method} {path} must be refused");
                }

                reached++;
            }
        }

        reached.ShouldBe(
            project.PartialEntities.Count * _singleRowRoutes.Length,
            "or this claim did not reach every operation of every partially-configured entity");
    }

    /// <summary>
    /// A permitted request <em>does</em> compose SQL — so the recorder the claim above reads is alive.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <see cref="DefaultDenyAsync"/>'s strongest assertion is that <c>world.Statements</c> is <em>empty</em>
    /// after a refusal, and a recorder that had quietly stopped recording would satisfy it forever. This is
    /// the other direction, and it is why the pair means something: the same recorder, on the same world,
    /// reports statements for a request that was allowed through.
    /// </remarks>
    internal static async Task TheStatementRecorderRecordsAsync(AlvoApiWorld world, GeneratedProject project)
    {
        var entity = project.PermissiveEntities[0];
        world.ClearStatements();

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{entity}", project.Admin());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        world.Statements.ShouldNotBeEmpty(
            "a permitted read must compose SQL, or the emptiness DefaultDenyAsync asserts proves nothing");
    }

    /// <summary>The five single-row routes, each named by the rule slot that admits it.</summary>
    private static readonly (string Operation, HttpMethod Method, string Suffix)[] _singleRowRoutes =
    [
        ("list", HttpMethod.Get, ""),
        ("get", HttpMethod.Get, "/{id}"),
        ("create", HttpMethod.Post, ""),
        ("update", HttpMethod.Patch, "/{id}"),
        ("delete", HttpMethod.Delete, "/{id}"),
    ];

    /// <summary>Create, read, list, patch and delete answer the same shapes for every entity.</summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    internal static async Task CrudShapeAsync(AlvoApiWorld world, GeneratedProject project)
    {
        // Every generated `ref` points at the first entity, and the first entity is always permissive, so
        // one row there is enough to satisfy every foreign key any other entity declares. Without it a
        // required ref would make the create a 422 and the CRUD claim would fail for the fixture's reason
        // rather than for the API's.
        var anchor = await CreateAsync(world, project, project.Entities[0], salt: 0);

        var visited = 0;
        foreach (var entity in project.PermissiveEntities)
        {
            var id = await CreateAsync(world, project, entity, salt: visited + 1, anchor: anchor);
            await ReadsAreShapedAsync(world, project, entity, id);
            await PatchIsAcceptedAsync(world, project, entity, id);
            await DeleteLeavesA404Async(world, project, entity, id);
            visited++;
        }

        visited.ShouldBe(project.PermissiveEntities.Count, "or the CRUD claim skipped an entity it should hold for");
    }

    /// <summary>
    /// The same <c>PUT</c> twice leaves the same row — and from two different starting states.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <b>The two starting states are the whole assertion.</b> One row is created with every field a caller
    /// may send; the other with only the mandatory ones. A <c>PUT</c> that merged rather than replaced would
    /// leave the first row carrying values the body never mentioned, so the two would differ — which is the
    /// property a merge cannot satisfy and a replace must. Repeating the same PUT then adds idempotence on
    /// top: the second call must change nothing.
    /// </remarks>
    internal static async Task ReplaceIsIdempotentAsync(AlvoApiWorld world, GeneratedProject project)
    {
        var entity = project.PermissiveEntities[0];
        var body = Body(project, entity, salt: 300, everyField: true, forCreate: false);

        // Sequential, and each row is deleted before the next is made: a `unique` field cannot be held by
        // two rows at once, so running the two starting states concurrently would answer 409 for a reason
        // that has nothing to do with idempotence.
        var fromFull = await ReplacedFromAsync(world, project, entity, everyField: true, salt: 100, body);
        var fromSparse = await ReplacedFromAsync(world, project, entity, everyField: false, salt: 200, body);

        fromFull.ShouldBe(
            fromSparse,
            "a replace must leave the same row whatever the row held before — a merge could not");
    }

    /// <summary>Creates a row in one starting state, replaces it twice, and reports what it became.</summary>
    private static async Task<string> ReplacedFromAsync(
        AlvoApiWorld world, GeneratedProject project, string entity, bool everyField, int salt, JsonObject body)
    {
        var id = await CreateAsync(world, project, entity, salt, everyField);
        var after = await PutTwiceAsync(world, project, entity, id, body);

        using var removed = await world.SendAsync(HttpMethod.Delete, $"/api/{entity}/{id}", project.Admin());
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the row has to go, or the next state collides with it");

        return after;
    }

    /// <summary>A replayed <c>Idempotency-Key</c> writes one row, not two.</summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// Counted from the table with <c>CountRowsAsync</c> and never from a list: a list is filtered by the
    /// caller's policy and by tenancy, so it would hide a second row rather than report it — which is the
    /// difference between measuring idempotence and measuring the read path.
    /// </remarks>
    internal static async Task IdempotencyKeyWritesOneRowAsync(AlvoApiWorld world, GeneratedProject project)
    {
        var entity = project.PermissiveEntities[0];
        var before = await world.CountRowsAsync(entity);
        var body = Body(project, entity, salt: 400, everyField: true, forCreate: true);
        var key = new[] { new KeyValuePair<string, string>("Idempotency-Key", $"replay-{project.Seed}") };

        using var first = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", project.Admin(), body: body, headers: key);
        using var replay = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", project.Admin(), body: body, headers: key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created, "a replay is answered with the first create's outcome");
        (await world.CountRowsAsync(entity)).ShouldBe(
            before + 1, "one key, one row — whatever the second response said");
    }

    /// <summary>A create, asserted to answer 201 with a followable <c>Location</c>.</summary>
    private static async Task<Guid> CreateAsync(
        AlvoApiWorld world, GeneratedProject project, string entity, int salt, bool everyField = true,
        Guid? anchor = null)
    {
        var body = Body(project, entity, salt, everyField, anchor);
        using var response = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", project.Admin(), body: body);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"create on '{entity}' was refused: {await response.Content.ReadAsStringAsync()}");
        var location = response.Headers.Location.ShouldNotBeNull($"a create on '{entity}' must name the row it made");

        return Guid.Parse(location.ToString().Split('/')[^1]);
    }

    /// <summary>The single read and the list read are both shaped as the document promises.</summary>
    private static async Task ReadsAreShapedAsync(
        AlvoApiWorld world, GeneratedProject project, string entity, Guid id)
    {
        using var single = await world.SendAsync(HttpMethod.Get, $"/api/{entity}/{id}", project.Admin());
        using var page = await world.SendAsync(HttpMethod.Get, $"/api/{entity}", project.Admin());

        single.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await single.ReadJsonObjectAsync())["id"]!.GetValue<string>().ShouldBe(id.ToString());

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var envelope = await page.ReadJsonObjectAsync();
        envelope.ContainsKey("items").ShouldBeTrue($"'{entity}' paged something that is not an envelope");
        envelope.ContainsKey("next").ShouldBeTrue($"'{entity}' page carries no cursor member");
        envelope.ContainsKey("count").ShouldBeTrue($"'{entity}' page carries no count member");
        envelope["items"]!.AsArray().Count.ShouldBeGreaterThan(0, $"'{entity}' page did not contain the row just created");
    }

    /// <summary>A patch of the one predictable field is accepted and is visible afterwards.</summary>
    private static async Task PatchIsAcceptedAsync(
        AlvoApiWorld world, GeneratedProject project, string entity, Guid id)
    {
        var field = FirstField(project, entity);
        var patch = new JsonObject { [field] = "patched" };

        using var response = await world.SendAsync(
            HttpMethod.Patch, $"/api/{entity}/{id}", project.Admin(), body: patch);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, $"patch on '{entity}' was refused: {await response.Content.ReadAsStringAsync()}");
        (await response.ReadJsonObjectAsync())[field]!.GetValue<string>().ShouldBe("patched");
    }

    /// <summary>A delete answers 204, and the row is then a problem document at 404.</summary>
    private static async Task DeleteLeavesA404Async(
        AlvoApiWorld world, GeneratedProject project, string entity, Guid id)
    {
        using var deleted = await world.SendAsync(HttpMethod.Delete, $"/api/{entity}/{id}", project.Admin());
        using var gone = await world.SendAsync(HttpMethod.Get, $"/api/{entity}/{id}", project.Admin());

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        gone.Content.Headers.ContentType!.MediaType.ShouldBe(
            "application/problem+json", "every refusal is a problem document, 404 included");
        (await gone.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldStartWith("https://alvo.dev/errors/");
    }

    /// <summary>Puts the same body twice and returns the resulting row, minus what a caller cannot control.</summary>
    private static async Task<string> PutTwiceAsync(
        AlvoApiWorld world, GeneratedProject project, string entity, Guid id, JsonObject body)
    {
        using var first = await world.SendAsync(HttpMethod.Put, $"/api/{entity}/{id}", project.Admin(), body: body.DeepClone().AsObject());
        using var second = await world.SendAsync(HttpMethod.Put, $"/api/{entity}/{id}", project.Admin(), body: body.DeepClone().AsObject());

        first.StatusCode.ShouldBe(HttpStatusCode.OK, $"replace on '{entity}' was refused: {await first.Content.ReadAsStringAsync()}");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await second.ReadJsonObjectAsync();

        // The id and the audit columns differ by construction — two rows have two ids, and an audited row
        // carries the instant it was written. Comparing them would make every case fail for a reason that is
        // not idempotence.
        foreach (var managed in _managed)
        {
            after.Remove(managed);
        }

        return after.ToJsonString();
    }

    /// <summary>The members a caller neither sends nor controls, and so cannot be compared across rows.</summary>
    private static readonly string[] _managed =
    [
        "id", "created_at", "created_by", "updated_at", "updated_by", "tenant_id",
    ];

    /// <summary>A body every field of which the entity actually declares.</summary>
    /// <param name="project">The generated project.</param>
    /// <param name="entity">The entity to build a body for.</param>
    /// <param name="salt">Makes every value distinct, so a <c>unique</c> field survives a second row.</param>
    /// <param name="everyField">Whether optional fields are sent as well as mandatory ones.</param>
    private static JsonObject Body(
        GeneratedProject project, string entity, int salt, bool everyField, Guid? anchor = null,
        bool forCreate = true)
    {
        var body = new JsonObject();

        // A create on a tenant-scoped entity has to ECHO the caller's tenant: the server verifies the value
        // rather than filling it in, which is what the published create schema admits the column for and
        // what DataApiAuthTests' own seeding does. A replace refuses the same member — its schema says so —
        // so the asymmetry is the contract and not a convenience here.
        if (forCreate && project.ScopedEntities.Contains(entity) && project.Tenant is { } tenant)
        {
            body["tenant_id"] = tenant.ToString();
        }
        foreach (var field in project.Fields[entity])
        {
            var declared = field.Value!.AsObject();
            var required = declared["required"]?.GetValue<bool>() ?? false;
            if (!required && !everyField)
            {
                continue;
            }

            // A `ref` is the one type whose value must already exist, so it takes the anchor row's id — and
            // is omitted where there is no anchor to point at, which is the case for the first entity (the
            // generator never gives it a ref) and for a body built before one exists.
            if (declared["type"]!.GetValue<string>() == "ref")
            {
                if (anchor is { } target)
                {
                    body[field.Key] = target.ToString();
                }

                continue;
            }

            body[field.Key] = Value(declared, salt);
        }

        return body;
    }

    /// <summary>One value of the declared type, distinct per salt.</summary>
    private static JsonNode Value(JsonObject field, int salt) =>
        field["type"]!.GetValue<string>() switch
        {
            "string" => JsonValue.Create(Text(field, salt))!,
            "text" => JsonValue.Create($"text-{salt}")!,
            "integer" => JsonValue.Create(salt + 1)!,
            "decimal" => JsonValue.Create(Number(field, salt))!,
            "boolean" => JsonValue.Create(salt % 2 == 0)!,
            "date" => JsonValue.Create("2026-01-01")!,
            "datetime" => JsonValue.Create("2026-01-01T00:00:00Z")!,
            "uuid" => JsonValue.Create(Deterministic(salt).ToString())!,
            "json" => new JsonObject { ["salt"] = salt },
            "enum" => JsonValue.Create(field["values"]![0]!.GetValue<string>())!,
            var other => throw new ArgumentOutOfRangeException(nameof(field), other, "no value for this field type"),
        };

    /// <summary>A decimal that fits the field's declared precision and scale.</summary>
    /// <remarks>
    /// <c>precision</c> is the <em>total</em> digit count and <c>scale</c> is how many of them are
    /// fractional, so a field declared <c>precision: 4, scale: 3</c> admits exactly one digit before the
    /// point — and the API refuses more with a 422 naming the field. The first version of this generator sent
    /// the salt verbatim and met that refusal, which is the same class of "the fixture is wrong, not the API"
    /// mistake as sending a string past its maxLength.
    /// </remarks>
    private static decimal Number(JsonObject field, int salt)
    {
        var precision = field["precision"]!.GetValue<int>();
        var scale = field["scale"]!.GetValue<int>();
        var whole = (int)Math.Pow(10, Math.Max(1, precision - scale));

        return salt % whole;
    }

    /// <summary>A string that honours the field's own <c>maxLength</c>.</summary>
    private static string Text(JsonObject field, int salt)
    {
        var value = $"value-{salt}";
        var max = field["maxLength"]?.GetValue<int>() ?? value.Length;

        return value.Length <= max ? value : value[..max];
    }

    /// <summary>A uuid that is a function of the salt, so a rerun writes the same bytes.</summary>
    private static Guid Deterministic(int salt) => Guid.Parse($"00000000-0000-0000-0000-{salt:D12}");

    /// <summary>The entity's first field, which the generator guarantees is a required string.</summary>
    private static string FirstField(GeneratedProject project, string entity) =>
        project.Fields[entity].First().Key;

    /// <summary>The batch or query body a route needs, or an empty object where any body will do.</summary>
    private static JsonObject Batch(HttpMethod method, string suffix) => suffix switch
    {
        "/batch" when method == HttpMethod.Delete => new JsonObject { ["ids"] = new JsonArray(Guid.NewGuid().ToString()) },
        "/batch" => new JsonObject { ["rows"] = new JsonArray(new JsonObject()) },
        _ => [],
    };
}
