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
[assembly: InternalsVisibleTo("MMLib.Alvo.Host.Tests")]
