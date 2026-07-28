using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]

// The Data API's own suite drives the feature end to end over HTTP, so it needs two internals that no
// public surface exposes: SchemaMigrationRunner (the code-first apply, which is what primes the applied
// schema the routes are generated from) and InMemoryApiKeyStore (to decorate a real, correctly
// authenticating key record as revoked — AlvoDevApiKey carries no revocation field, and replacing that
// store outright would stop the suite exercising authentication at all).
[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Tests")]
