using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]

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
