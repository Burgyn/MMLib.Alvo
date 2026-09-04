using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Enriches the OpenAPI document with everything a generated Data API endpoint cannot say for itself: the
/// declared shape of a row, the query surface a list accepts, the statuses each operation answers with, and
/// the header behaviours a caller cannot infer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is generated from the endpoints that were actually mapped, and this only enriches
/// them.</b> Emitting it from <see cref="SchemaModel"/> instead would document the <em>schema</em> rather
/// than the <em>routes</em>, so a route-mapping bug would never appear in it — while §2.1's acceptance
/// criterion is that the document is consistent with actual behaviour. So the walk starts from
/// <see cref="OpenApiDocumentTransformerContext.DescriptionGroups"/>, and an entity contributes nothing
/// unless an endpoint for it exists.
/// </para>
/// <para>
/// <b>Only Alvo's own endpoints are touched, identified by <see cref="DataApiOperationMetadata"/>.</b> An
/// embedded host's document carries its endpoints beside Alvo's, and the marker is the one thing that
/// distinguishes them without matching on a path prefix a host can configure. It is the same marker
/// <c>DataApiEndpoints.Protect</c> attaches in the call that attaches the authorization filter, so an
/// endpoint this transformer describes is by construction one that is gated.
/// </para>
/// <para>
/// <b>The enrichment is substantial rather than cosmetic because the generated delegates are weakly
/// typed.</b> Their payload is a dictionary and their query string is parsed by hand, so ApiExplorer sees a
/// body of <c>object</c> and no query parameters at all — a document nobody could generate a client from.
/// Everything of substance therefore comes from the applied schema, through
/// <see cref="SchemaComponentBuilder"/>.
/// </para>
/// <para>
/// <b>A missing policy or schema entry for a routed entity throws.</b> Both are primed from the same apply as
/// the route literals, so an entity with endpoints and no policy is a broken framework invariant, not a
/// configuration a host can reach — and a document that quietly omitted the entity would hide it. It is
/// <c>IAlvoData</c>'s fifth failure family, rendered by the host as a 500 on the document endpoint.
/// </para>
/// </remarks>
/// <param name="policies">Holds the compiled catalog the <c>hidden</c>/<c>readOnly</c> field flags are read from.</param>
/// <param name="schema">The applied schema, the one authority on an entity's declared shape.</param>
/// <param name="options">The API options the paging parameters publish their bounds from.</param>
/// <param name="auth">
/// The auth options the credential and tenant header <em>names</em> come from. Both are configurable — an
/// embedded host may have to move them out of the way of its own — so a document that spelled either as a
/// literal would tell a client to send a header the host does not read.
/// </param>
internal sealed class AlvoDocumentTransformer(
    IPolicyCatalogProvider policies,
    ISchemaRegistry schema,
    IOptions<AlvoApiOptions> options,
    IOptions<Auth.AlvoAuthOptions> auth) : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var generated = Generated(context);
        if (generated.Count == 0)
        {
            return Task.CompletedTask;
        }

        var views = Views(generated);
        var entities = (IReadOnlyList<EntitySchema>)[.. views.Select(view => view.Schema)];
        var operations = Operations(generated, entities);

        Overview(document);
        ProblemComponents.AddTo(document);
        document.AddComponent(CredentialScheme, Credential());
        Reusable(document, operations);
        foreach (var view in views)
        {
            Describe(document, view);
        }

        foreach (var endpoint in generated)
        {
            Enrich(document, views[endpoint.Marker.Entity], endpoint);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One entity's declared shape and its field flags, resolved once for the whole document.
    /// </summary>
    /// <param name="Schema">The entity as the applied schema declares it.</param>
    /// <param name="Hidden">Every field carrying a <c>hidden</c> flag, as the union across roles.</param>
    /// <param name="ReadOnly">Every field carrying a <c>readOnly</c> flag, as the union across roles.</param>
    private readonly record struct EntityView(
        EntitySchema Schema, IReadOnlySet<string> Hidden, IReadOnlySet<string> ReadOnly);

    /// <summary>
    /// Every entity the mapped endpoints serve, in first-seen order, each resolved exactly once (#126).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both sources are read once for the whole document, not once per endpoint.</b> The Data API maps
    /// five endpoints per entity, and resolving each entity's schema and flags inside the per-endpoint walk
    /// meant <c>6N</c> resolutions: <c>N</c> from this list plus <c>5N</c> from the enrichment. The schema
    /// lookup was a linear scan by name, so that was <c>O(N²)</c> comparisons, and each flag resolution
    /// allocated <b>two</b> fresh sets — <c>12N</c> of them. The document is rebuilt per request on an
    /// endpoint that needs no credential, so all of it was per-request work.
    /// </para>
    /// <para>
    /// <b>The schema is indexed by name once, rather than scanned per entity.</b> Reading it once and still
    /// calling <c>FirstOrDefault</c> per entity would leave <c>O(N²)</c> comparisons behind a smaller
    /// constant — a sixth of the work and the same complexity — which is not what this is for.
    /// </para>
    /// <para>
    /// <b>One behavioural edge the index does change.</b> Two entities of the same name in one
    /// <see cref="SchemaModel"/> used to resolve to whichever came first; indexing them throws instead. That
    /// state is unreachable through a descriptor — <c>entities</c> is a JSON object keyed by name, so the
    /// parser cannot produce it — and throwing is the better answer for a schema that cannot say which
    /// entity it means, but it is a difference rather than a pure hoist and is recorded rather than left to
    /// be discovered.
    /// </para>
    /// <para>
    /// <b>An absent entity or policy still throws, and that is the point of resolving here.</b> Both are
    /// primed by the same descriptor apply that produced the route literals, so either one missing means the
    /// endpoint table and the applied state disagree — a broken framework invariant rather than a
    /// configuration a host can reach. Doing the lookups here rather than lazily keeps that failure loud and
    /// keeps it at one place; a <c>TryGetValue</c> that skipped the entity would answer with a document
    /// quietly missing it.
    /// </para>
    /// </remarks>
    /// <param name="generated">The mapped Data API endpoints.</param>
    private EntityViews Views(IEnumerable<Endpoint> generated)
    {
        // The schema is read BEFORE the catalog, and that order is the fail-closed one. By default both are
        // reads of the SAME holder — Rules/Setup.cs registers ISchemaRegistry as a factory resolving
        // IPolicyCatalogProvider — which is republished monotonically, so the catalog is always the
        // same-or-newer half of the pair: an older field list judged by a newer, possibly stricter mask.
        // Swapping these two lines flips it to the disclosing direction, where a newer field list is judged
        // by an older mask and can publish the name of a field the current policy hides. A host that
        // registers its own ISchemaRegistry (which that registration's remarks allow) makes these two
        // independent holders, and the monotonic argument no longer applies — the ordering is then merely
        // the better guess rather than a guarantee.
        var declared = schema.GetSchema().Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        var catalog = policies.Current;
        var views = new EntityViews();

        foreach (var name in generated.Select(endpoint => endpoint.Marker.Entity))
        {
            views.Resolve(name, declared, catalog);
        }

        return views;
    }

    /// <summary>
    /// The entities of one document: resolved once each, addressable by name, and enumerable in the order the
    /// endpoints first named them.
    /// </summary>
    /// <remarks>
    /// <b>The order is held explicitly rather than taken from the dictionary.</b> The walk this replaced used
    /// <c>Distinct</c>, whose first-seen order is contractual, and the order reaches the published document —
    /// it is the order components are registered in, and the document has a Verify baseline. <c>Dictionary</c>
    /// enumeration order is not part of its contract, so relying on it would make a byte-stable document an
    /// accident of the runtime's current bucket layout rather than a property of this code.
    /// </remarks>
    private sealed class EntityViews : IEnumerable<EntityView>
    {
        private readonly Dictionary<string, EntityView> _byName = new(StringComparer.Ordinal);
        private readonly List<EntityView> _inOrder = [];

        internal EntityView this[string entity] => _byName[entity];

        /// <summary>Resolves <paramref name="entity"/> unless it has already been resolved.</summary>
        /// <param name="entity">The entity name an endpoint was mapped for.</param>
        /// <param name="declared">Every entity the applied schema declares, by name.</param>
        /// <param name="catalog">The compiled catalog, read once for the whole document.</param>
        internal void Resolve(
            string entity, IReadOnlyDictionary<string, EntitySchema> declared, PolicyCatalog? catalog)
        {
            if (_byName.ContainsKey(entity))
            {
                return;
            }

            var view = ViewOf(entity, declared, catalog);
            _byName.Add(entity, view);
            _inOrder.Add(view);
        }

        public IEnumerator<EntityView> GetEnumerator() => _inOrder.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Resolves one entity against the applied schema and the compiled catalog.</summary>
    /// <param name="entity">The entity name an endpoint was mapped for.</param>
    /// <param name="declared">Every entity the applied schema declares, by name — indexed once per document.</param>
    /// <param name="catalog">The compiled catalog, read once for the whole document.</param>
    private static EntityView ViewOf(
        string entity, IReadOnlyDictionary<string, EntitySchema> declared, PolicyCatalog? catalog)
    {
        if (!declared.TryGetValue(entity, out var found))
        {
            throw new InvalidOperationException(
                $"Entity '{entity}' has generated endpoints but is absent from the applied schema. The routes were "
                + "mapped from that schema, so this is a framework invariant rather than a configuration error.");
        }

        if (catalog is null || !catalog.TryGetEntity(entity, out var policy))
        {
            throw new InvalidOperationException(
                $"Entity '{entity}' has generated endpoints but no compiled policy. Both are primed by one "
                + "descriptor apply, so this is a framework invariant rather than a configuration error.");
        }

        return new EntityView(
            found,
            policy.Hidden.Keys.ToHashSet(StringComparer.Ordinal),
            policy.ReadOnly.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>One mapped Data API endpoint: its API description and the marker that identified it.</summary>
    /// <param name="Description">What ApiExplorer reported for the endpoint.</param>
    /// <param name="Marker">The entity and operation the endpoint was mapped for.</param>
    private sealed record Endpoint(ApiDescription Description, DataApiOperationMetadata Marker);

    /// <summary>Every Alvo-generated endpoint in this document, in the order ApiExplorer reported them.</summary>
    private static List<Endpoint> Generated(OpenApiDocumentTransformerContext context) =>
        [.. context.DescriptionGroups
            .SelectMany(group => group.Items)
            .Select(description => (description, marker: Marker(description)))
            .Where(pair => pair.marker is not null)
            .Select(pair => new Endpoint(pair.description, pair.marker!))];

    private static DataApiOperationMetadata? Marker(ApiDescription description) =>
        description.ActionDescriptor.EndpointMetadata.OfType<DataApiOperationMetadata>().FirstOrDefault();

    /// <summary>
    /// Every generated endpoint's kind, paired with the entity it serves — the exact scope
    /// <see cref="Reusable"/> reads to decide which shared components are really referenced by something.
    /// </summary>
    /// <remarks>
    /// The <em>kind</em> and not the operation, because two kinds resolve to one operation: collapsing them
    /// here would make a component referenced only by the query endpoint look like an orphan, and would make
    /// one referenced only by the list endpoint look as though both used it.
    /// </remarks>
    private static IReadOnlyList<(DataApiEndpointKind Kind, EntitySchema Entity)> Operations(
        IEnumerable<Endpoint> generated, IReadOnlyList<EntitySchema> entities)
    {
        var byName = entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        return [.. generated.Select(endpoint => (endpoint.Marker.Kind, byName[endpoint.Marker.Entity]))];
    }

    /// <summary>
    /// Adds the document-level prose, appending rather than replacing whatever the host already wrote.
    /// </summary>
    /// <remarks>
    /// An embedded host owns its own <c>info</c> — its title and version name the host's API, not Alvo's — so
    /// overwriting the description would delete something a host authored. Appending is the only composition
    /// that is safe in both modes; in standalone mode there is nothing to append to.
    /// </remarks>
    private static void Overview(OpenApiDocument document)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Description = string.IsNullOrWhiteSpace(document.Info.Description)
            ? DataApiDocumentation.Overview
            : document.Info.Description + "\n\n" + DataApiDocumentation.Overview;
    }

    /// <summary>
    /// Registers everything that is the same on every generated route: each refusal response, each response
    /// header, and each request parameter whose meaning does not depend on the entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not an optimization — it is what makes the document readable and a generated client sane.</b> Inlined,
    /// the six refusals and eleven shared parameters are repeated once per operation, which for a
    /// ten-entity descriptor is the same paragraph fifty times; a code generator reading it emits fifty
    /// identical response types. Publishing each once and referencing it is what OpenAPI's component maps are
    /// for, and it is why a reviewer reads each sentence of this contract exactly once.
    /// </para>
    /// <para>
    /// <b>The six refusals are unconditional; the headers and the per-host parameters are not.</b> Every
    /// generated endpoint can answer 401 and 403 at least, so the refusal components are never orphans. A
    /// header or a parameter that depends on a trait not every descriptor has — <c>ETag</c>/<c>ifNoneMatch</c>
    /// on an audited entity, <c>tenant</c> on a tenant-scoped one — is published only when
    /// <paramref name="operations"/> actually contains one that references it, through
    /// <see cref="DataApiHeaders.AddTo"/> and <see cref="DataApiParameters.UsedSharedIds"/>. A descriptor with
    /// no audited or tenant-scoped entity used to ship both anyway — a component map is a library, but an
    /// entry nothing in the document could ever point at is the same defect the <c>ProducesProblem</c>
    /// deviation exists to avoid for a schema.
    /// </para>
    /// </remarks>
    /// <param name="document">The document being built.</param>
    /// <param name="operations">Every generated endpoint's kind and the entity it serves.</param>
    private void Reusable(
        OpenApiDocument document, IReadOnlyList<(DataApiEndpointKind Kind, EntitySchema Entity)> operations)
    {
        DataApiHeaders.AddTo(document, operations);
        foreach (var refusal in DataApiDocumentation.SharedRefusals)
        {
            document.AddComponent(refusal.SharedId!, Response(refusal, entity: null, document));
        }

        var used = DataApiParameters.UsedSharedIds(operations);
        foreach (var (id, parameter) in DataApiParameters.Shared(options.Value, auth.Value.TenantHeaderName))
        {
            if (used.Contains(id))
            {
                document.AddComponent(id, parameter);
            }
        }
    }

    /// <summary>Registers one entity's schema components and the tag its operations are grouped under.</summary>
    /// <remarks>
    /// <para>
    /// <b>An instance method because of one component.</b> The query body's <c>limit</c> property carries
    /// <see cref="AlvoApiOptions.MaxPageSize"/>, and <see cref="SchemaComponentBuilder"/> is constructed
    /// with a schema view and no options — so the component is built here, where the options already are,
    /// rather than threading them through a builder to serve one member.
    /// </para>
    /// <para>
    /// The tag <em>name</em> comes from <c>DataApiEndpoints</c>' own <c>WithTags</c>, because ApiExplorer's
    /// default for a minimal API is the <em>host assembly's</em> name — which would group Alvo's endpoints
    /// under whatever executable happens to be running and make the document's content depend on it.
    /// </para>
    /// <para>
    /// <b>The description is written onto the tag the framework already created, not added beside it.</b>
    /// <c>WithTags</c> has already put a bare <c>OpenApiTag</c> with this name in the document's set, and
    /// <c>OpenApiTag</c>'s equality is by name — so adding a second one carrying the description was silently
    /// discarded and every tag published as name-only. Measured, not theorised: the first document built this
    /// way had no tag descriptions at all while the descriptor described every entity.
    /// </para>
    /// </remarks>
    private void Describe(OpenApiDocument document, EntityView view)
    {
        new SchemaComponentBuilder(view.Schema, view.Hidden, view.ReadOnly).AddTo(document);
        document.AddComponent(
            SchemaComponentBuilder.QueryId(view.Schema.Name),
            DataApiParameters.QueryBody(view.Schema, view.Hidden, options.Value));
        Tag(document, view.Schema.Name).Description = view.Schema.Description;
    }

    /// <summary>The document-level tag <c>DataApiEndpoints.Documenting</c>'s own <c>WithTags</c> already created.</summary>
    /// <remarks>
    /// Absence throws rather than falling back to creating one here. <c>Describe</c> only ever runs for an
    /// entity with generated endpoints, and every one of them ran <c>WithTags(entity.Name)</c> before
    /// ApiExplorer built this document — which is what seeds <see cref="OpenApiDocument.Tags"/> before any
    /// transformer runs. A fallback that created a fresh tag here was accordingly a branch nothing could
    /// reach: a real absence would mean the endpoint table and this document disagree about what was mapped,
    /// which is the same framework invariant <see cref="Find"/> and <see cref="ViewOf"/> already fail loudly
    /// on rather than paper over.
    /// </remarks>
    private static OpenApiTag Tag(OpenApiDocument document, string entity) =>
        document.Tags?.FirstOrDefault(tag => string.Equals(tag.Name, entity, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"The OpenAPI document carries no tag named '{entity}', although its endpoints were mapped with "
            + "WithTags(entity.Name). The document and the endpoint table disagree about what was mapped.");

    /// <summary>Rewrites one operation into the contract its endpoint actually implements.</summary>
    private static void Enrich(OpenApiDocument document, EntityView view, Endpoint endpoint)
    {
        var entity = view.Schema;
        var operation = Find(document, endpoint);
        var flags = (view.Hidden, view.ReadOnly);

        operation.Summary = DataApiDocumentation.SummaryOf(endpoint.Marker.Kind, entity.Name);
        operation.Description = DataApiDocumentation.DescriptionOf(endpoint.Marker.Kind, entity);
        operation.OperationId = OperationId(endpoint.Marker);
        operation.Tags = new HashSet<OpenApiTagReference> { new(entity.Name, document) };
        operation.Parameters = DataApiParameters.For(
            endpoint.Marker.Kind, entity, flags.Hidden, document);
        operation.RequestBody = RequestBody(endpoint.Marker, entity, document);
        operation.Responses = Responses(endpoint.Marker, entity, document);
        operation.Security = Security(document);
    }

    /// <summary>The component id of the API-key security scheme every generated operation references.</summary>
    /// <remarks>
    /// Lower camel case, so it cannot collide with an entity's component id — the same reasoning as
    /// <see cref="ProblemComponents.DocumentId"/>. Security schemes live in their own component map, so the
    /// collision is only with itself; keeping one convention across all component ids is cheaper than
    /// remembering which maps are separate.
    /// </remarks>
    private const string CredentialScheme = "alvoApiKey";

    /// <summary>
    /// The credential a caller presents: an API key in a host-configured request header.
    /// </summary>
    /// <remarks>
    /// Without this the document has a documented 401 and no way to satisfy it — a generated client would have
    /// no place to put a key, which defeats the whole reason the document is published.
    /// </remarks>
    private OpenApiSecurityScheme Credential() => new()
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = auth.Value.HeaderName,
        Description =
            "An Alvo API key, presented as `<keyId>.<secret>`. A key that cannot be used — unknown, revoked, "
            + "expired, malformed, or issued for another tenant — is a 401 with one wording for all of them. "
            + "The header's name is host configuration; this is the name this host reads.",
    };

    /// <summary>
    /// The security requirement each generated operation carries: this scheme, <b>or nothing</b>.
    /// </summary>
    /// <remarks>
    /// The empty alternative is not a hedge. An anonymous caller is a first-class one here — a descriptor may
    /// legitimately admit <c>anon</c> — and the 401 is for a credential that was <em>presented</em> and cannot
    /// be used, never for one that was absent. Declaring the scheme as mandatory would tell a client that a key
    /// is required, which for such a descriptor is false.
    /// </remarks>
    private static List<OpenApiSecurityRequirement> Security(OpenApiDocument document) =>
    [
        new(),
        new() { [new OpenApiSecuritySchemeReference(CredentialScheme, document)] = [] },
    ];

    /// <summary>
    /// The document operation the mapped endpoint produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup is over <see cref="DocumentPath"/>, because the two spellings of one route differ:
    /// <see cref="ApiDescription.RelativePath"/> keeps the route constraint and the leading slash is absent,
    /// while a document path key is <c>/api/owners/{id}</c>.
    /// </para>
    /// <para>
    /// Absence throws rather than being skipped. The description came out of this document's own generation,
    /// so an operation missing for it means the two disagree about what was mapped — precisely the drift
    /// <c>OpenApiDocumentTests.Every_mapped_route_appears_in_the_document_and_nothing_else_does</c> exists to
    /// refuse, and skipping would leave the operation in the document undescribed instead of failing.
    /// </para>
    /// </remarks>
    private static OpenApiOperation Find(OpenApiDocument document, Endpoint endpoint)
    {
        var path = DocumentPath(endpoint.Description);
        var method = HttpMethod.Parse(endpoint.Description.HttpMethod!);

        return document.Paths.TryGetValue(path, out var item)
            && item.Operations is { } operations
            && operations.TryGetValue(method, out var operation)
            ? operation
            : throw new InvalidOperationException(
                $"The OpenAPI document carries no '{method} {path}' operation, although ApiExplorer reported "
                + "the endpoint. The document and the endpoint table disagree about what was mapped.");
    }

    /// <summary>
    /// One route template as the document keys it: a leading slash, and every route parameter reduced to its
    /// name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two spellings are not the same string, which is why this exists.</b> Alvo maps
    /// <c>/api/owners/{id:guid}</c> and ApiExplorer reports that template verbatim, while the document —
    /// correctly, since OpenAPI has no notion of a routing constraint — keys the path <c>/api/owners/{id}</c>.
    /// Comparing the raw templates found no operation at all.
    /// </para>
    /// <para>
    /// Scanned rather than matched with a regular expression, so there is no pattern to bound and no timeout to
    /// justify on a path this walk performs once per endpoint at document-build time. Text this does not
    /// recognise is left as it is: the result then matches no path key and <see cref="Find"/> throws, which is
    /// the loud outcome rather than an operation silently left undescribed.
    /// </para>
    /// </remarks>
    /// <param name="description">The endpoint's API description.</param>
    private static string DocumentPath(ApiDescription description)
    {
        var template = description.RelativePath ?? string.Empty;
        var path = new System.Text.StringBuilder(template.Length + 1).Append('/');
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            var close = open < 0 ? -1 : template.IndexOf('}', open);
            if (close < 0)
            {
                return path.Append(template, index, template.Length - index).ToString();
            }

            path.Append(template, index, open - index + 1)
                .Append(ParameterName(template[(open + 1)..close]))
                .Append('}');
            index = close + 1;
        }

        return path.ToString();
    }

    /// <summary>A route parameter's name, with any constraint, default or optional marker cut off it.</summary>
    /// <param name="parameter">The text between the braces.</param>
    private static string ParameterName(string parameter)
    {
        var cut = parameter.IndexOfAny([':', '?', '=']);
        return cut < 0 ? parameter : parameter[..cut];
    }

    /// <summary>
    /// The operation's stable identifier, which a generated client turns into a method name.
    /// </summary>
    /// <remarks>
    /// <c>&lt;entity&gt;.&lt;operation&gt;</c>, and unique by construction: an entity name cannot contain a
    /// dot (the descriptor's grammar is <c>^[a-z][a-z0-9_]{0,62}$</c>), so no two entity/operation pairs can
    /// collide. It is set here rather than with <c>WithName</c> on the route, because an endpoint <em>name</em>
    /// must be unique across the whole host and would make Alvo's mapping able to collide with a host's own
    /// named endpoint at startup.
    /// </remarks>
    private static string OperationId(DataApiOperationMetadata marker) =>
        $"{marker.Entity}.{marker.Kind.ToWireName()}";

    /// <summary>The body a write accepts, or <see langword="null"/> for the three operations that take none.</summary>
    private static OpenApiRequestBody? RequestBody(
        DataApiOperationMetadata marker, EntitySchema entity, OpenApiDocument document) =>
        BodyComponent(marker.Kind, entity.Name) is not { } component
            ? null
            : new OpenApiRequestBody
            {
                Required = true,
                Description = BodyDescription(marker.Kind),
                Content = Json(new OpenApiSchemaReference(component, document)),
            };

    private static string? BodyComponent(DataApiEndpointKind kind, string entity) => kind switch
    {
        DataApiEndpointKind.Create => SchemaComponentBuilder.CreateId(entity),
        DataApiEndpointKind.Update => SchemaComponentBuilder.PatchId(entity),
        DataApiEndpointKind.Query => SchemaComponentBuilder.QueryId(entity),
        DataApiEndpointKind.BatchCreate => SchemaComponentBuilder.BatchCreateId(entity),
        DataApiEndpointKind.BatchUpdate => SchemaComponentBuilder.BatchUpdateId(entity),
        DataApiEndpointKind.BatchDelete => SchemaComponentBuilder.BatchDeleteId(entity),
        _ => null,
    };

    /// <summary>What the body carries, which is a row on a write and the query parameters on a read.</summary>
    private static string BodyDescription(DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.Query =>
            "The query parameters, as an object. An empty object reads the first page with no filter.",
        DataApiEndpointKind.BatchCreate or DataApiEndpointKind.BatchUpdate
            or DataApiEndpointKind.BatchDelete =>
            "The rows to write, under a 'rows' array. A batch is one transaction: every row is written, or "
            + "none is.",
        _ => "The row to write, as the entity's declared fields.",
    };

    /// <summary>
    /// Every response the operation can answer with, built from <see cref="DataApiDocumentation"/>'s catalogue
    /// — the same table <c>DataApiEndpoints</c> attaches as endpoint metadata.
    /// </summary>
    /// <remarks>
    /// It <em>replaces</em> whatever ApiExplorer inferred rather than merging into it. ApiExplorer's inference
    /// for an <c>IResult</c>-returning delegate is an untyped 200 and nothing else, so merging would leave a
    /// 200 with no schema beside the real ones; and the catalogue is the authority for which statuses exist, so
    /// anything else present is by definition not one this endpoint answers with.
    /// </remarks>
    private static OpenApiResponses Responses(
        DataApiOperationMetadata marker, EntitySchema entity, OpenApiDocument document)
    {
        var responses = new OpenApiResponses();
        foreach (var response in DataApiDocumentation.ResponsesFor(marker.Kind, entity))
        {
            responses[Text(response.Status)] = response.SharedId is { } shared
                ? Referenced(response, shared, document)
                : Response(response, entity, document);
        }

        return responses;
    }

    /// <summary>
    /// A reference to one shared refusal component, carrying this operation's own narrowing of it when the
    /// catalogue supplied one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sibling <c>description</c> is OpenAPI 3.1's own mechanism, not a workaround.</b> The Reference
    /// Object takes <c>description</c> beside <c>$ref</c> and it "SHOULD override that of the referenced
    /// component" — so one operation can say what the status means <em>there</em> while the shape, the body and
    /// the wording every other route shares stay in the single component. Inlining the whole response for the
    /// entities that need a narrower sentence would give up
    /// <c>DataApiDocumentation.Response.SharedId</c>'s bargain for a sentence, and a reader comparing two
    /// routes' 412 would have to diff two paragraphs to find the one clause that differs.
    /// </para>
    /// <para>
    /// Left <see langword="null"/> the reference serialises as a bare <c>$ref</c>, which is what all but the
    /// version-less writes' 412 do.
    /// </para>
    /// </remarks>
    /// <param name="response">The catalogue entry being published.</param>
    /// <param name="shared">The <c>components.responses</c> id it is published under.</param>
    /// <param name="document">The document the component lives in.</param>
    private static OpenApiResponseReference Referenced(
        DataApiDocumentation.Response response, string shared, OpenApiDocument document) =>
        new(shared, document) { Description = response.SharedNarrowing };

    /// <summary>
    /// One response object: its description, its body, and the headers it carries.
    /// </summary>
    /// <param name="response">The response being described.</param>
    /// <param name="entity">
    /// The entity, or <see langword="null"/> when building a shared refusal component — which has no entity and
    /// needs none, because a refusal carries the problem document rather than a row, and no <c>ETag</c>.
    /// </param>
    /// <param name="document">The document the body's schema is referenced from.</param>
    private static OpenApiResponse Response(
        DataApiDocumentation.Response response, EntitySchema? entity, OpenApiDocument document) =>
        new()
        {
            Description = response.Description,
            Content = Content(response, entity, document),
            Headers = DataApiHeaders.For(response, entity, document),
        };

    private static Dictionary<string, OpenApiMediaType>? Content(
        DataApiDocumentation.Response response, EntitySchema? entity, OpenApiDocument document) =>
        response.Body switch
        {
            DataApiDocumentation.ResponseBody.Row =>
                Json(new OpenApiSchemaReference(SchemaComponentBuilder.RowId(Named(entity).Name), document)),
            DataApiDocumentation.ResponseBody.Page =>
                Json(new OpenApiSchemaReference(SchemaComponentBuilder.PageId(Named(entity).Name), document)),
            DataApiDocumentation.ResponseBody.Batch =>
                Json(new OpenApiSchemaReference(
                    SchemaComponentBuilder.BatchResultId(Named(entity).Name), document)),
            DataApiDocumentation.ResponseBody.Problem =>
                Media(ProblemMediaType, new OpenApiSchemaReference(ProblemComponents.DocumentId, document)),
            _ => null,
        };

    /// <summary>
    /// The entity a row-carrying response belongs to. A response whose body is a row and whose entity is
    /// unknown cannot be built, and a shared refusal component never asks for one — so this is a framework
    /// invariant rather than a case to fall back from.
    /// </summary>
    private static EntitySchema Named(EntitySchema? entity) =>
        entity ?? throw new InvalidOperationException(
            "A response carrying a row was built with no entity. Only the shared refusal components are built "
            + "without one, and none of them carries a row.");

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The media type RFC 9457 §3 requires of a problem document.</summary>
    internal const string ProblemMediaType = "application/problem+json";

    /// <summary>The media type every success body uses.</summary>
    internal const string JsonMediaType = "application/json";

    private static Dictionary<string, OpenApiMediaType> Json(IOpenApiSchema schema) =>
        Media(JsonMediaType, schema);

    private static Dictionary<string, OpenApiMediaType> Media(string mediaType, IOpenApiSchema schema) =>
        new(StringComparer.Ordinal) { [mediaType] = new OpenApiMediaType { Schema = schema } };
}
