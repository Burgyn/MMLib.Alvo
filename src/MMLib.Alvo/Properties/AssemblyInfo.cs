using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]

// The SQLite leg of the 10 000-event chaos criterion, which is its own assembly for the same reason the
// PostgreSQL leg below is: it is one criterion measured on two engines, the suite is one linked source file
// (test/_shared/events), and a numeric criterion that costs ~27 s belongs in ring2 rather than in "after every
// small step". A project-name suffix is the only tier discriminator the ring scripts have, so the tier and the
// assembly are the same decision.
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests.Integration")]

// The Data API's own suite drives the feature end to end over HTTP, so it needs two internals that no
// public surface exposes: SchemaMigrationRunner (the code-first apply, which is what primes the applied
// schema the routes are generated from) and InMemoryApiKeyStore (to decorate a real, correctly
// authenticating key record as revoked — AlvoDevApiKey carries no revocation field, and replacing that
// store outright would stop the suite exercising authentication at all).
[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Tests")]

// The same suite on a real PostgreSQL engine (#19's DoD is "green on SQLite + Postgres"). It is the *same
// sources* — test/_shared/api is linked into both projects, so the world compiled here reaches exactly the
// two internals above and nothing further. A separate name is unavoidable: InternalsVisibleTo names an
// assembly, and the two legs are two assemblies because ring0 must stay Docker-free.
[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Tests.Integration")]

// #26's API invariant suite, on the same terms and for the same reason: it compiles the same
// test/_shared/api sources, so it reaches exactly the two internals named above and nothing further. It is a
// third assembly because the tier discriminator in this repository is the project name, and this suite boots
// twenty generated and example descriptors — which belongs before a PR, not after every small step.
[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Invariants.Tests.Integration")]

// The PostgreSQL leg of the event acceptance criteria, for the same reason and on the same terms: the criteria
// are "green on SQLite + Postgres", the world is one linked source file (test/_shared/events), and it reaches
// exactly three internals — OutboxDispatcher.PumpOneBatchAsync for a deterministic drain, WebhookDelivery's
// named-client constant, and EventLog.ActionExecuted's name. Everything else it touches is public surface. The
// two legs are two assemblies because ring0 must stay Docker-free.
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.PostgreSql.Tests.Integration")]

// The standalone host's own recovery facts, for one internal: WebhookDelivery's named-client constant, which is
// what lets them substitute the socket under the production HTTP client instead of asserting against a
// hard-coded client name. The alternative was to repeat the string, and two authorities for the name of the
// client a delivery goes through is how a fact comes to substitute a handler nothing resolves.
// And a second: CapabilityReport, which DeclaredSlotsAgreementTests holds against the dashboard's own reader of
// the warned slots — the one suite that sees both assemblies' internals (docs/todo-admin.md §8d item 27).
[assembly: InternalsVisibleTo("MMLib.Alvo.Host.Tests")]

// The analyzer's parity fact reads the boot ban's own list (HookDependencies), so ALVO1001's reach list is held a
// superset of what the core refuses at start, rather than a copy that drifts from it.
[assembly: InternalsVisibleTo("MMLib.Alvo.Analyzers.Tests")]

// The assistant eval's reject_negative_price grader (HX-11), for three internals: DescriptorToSchemaMapper, CelCompiler
// and CelInterpreter. A field's validation is graded by what the core makes of it — the canonical CHECK expression,
// evaluated against a negative price and a zero one — never by its spelling, which has as many right forms as CEL
// has ways to say "not negative". The eval is a measurement project in no ring, never packed or shipped.
[assembly: InternalsVisibleTo("MMLib.Alvo.Ai.Eval")]

// End-user sign-in (spec 2026-11-13 §11.1, S2 D1): the first shipped product assembly to see the core's internals.
// What MMLib.Alvo.Accounts consumes is core by the spec — CallerResolution and its person-resolver key,
// ProblemResultFactory, the CORS and rate-limit filters, membership resolution and SignInPolicy — and publishing those
// would make five seams public for one in-family consumer, which un-publishing could never take back. The grant reaches
// every other internal here too, so an architecture fact in the Accounts suite (AccountsArchitectureTests) holds
// that it references none of the security-core members and none of the ambient flow's setters the Abstractions grants
// already police. The same forgeability caveat applies: an unsigned grant is a name match.
[assembly: InternalsVisibleTo("MMLib.Alvo.Accounts")]

// The standalone host, for one internal: ClientPartition, the per-client key its dashboard credential budget is charged
// under — the same key the token endpoint's budget uses, so the two cannot partition differently (it was a verbatim
// copy until the S2 review of Tasks 10-11). A linked source file was tried and refused: the OpenAPI XML-comment
// generator reads both assemblies' documentation and two types with one documentation id fail every document request.
// The host is IsPackable=false and ships only in the image; an architecture fact in its suite
// (HostInternalGrantTests) holds that ClientPartition is the only core internal it references. The same forgeability
// caveat applies: an unsigned grant is a name match.
[assembly: InternalsVisibleTo("MMLib.Alvo.Host")]

// And to its suite, which compiles the Data API's test world (test/_shared/api) to drive a signed-in person through the
// generated routes, so it reaches the two internals that world needs (SchemaMigrationRunner, InMemoryApiKeyStore) on
// the MMLib.Alvo.Api.Tests terms above, plus what it stubs the package's core seams with.
[assembly: InternalsVisibleTo("MMLib.Alvo.Accounts.Tests")]
