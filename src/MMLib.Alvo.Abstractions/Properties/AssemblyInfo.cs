using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MMLib.Alvo")]

// DestructiveChangeGuard moved here from the core when MigrationResult.EnsureApplied started needing the
// same summary, and its facts stayed in the core's suite beside SchemaMigrationRunnerTests — which is the
// other in-repo caller and the reason the formatter exists at all. One grant, to the one assembly that
// already tests both sides of it; the same forgeability caveat as every other InternalsVisibleTo in the
// family applies (an unsigned grant is a name match, not a proof).
[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]

// AlvoFrameworkTables names the framework's own bookkeeping tables, and the two assemblies that need
// that answer cannot see each other: the EF adapter creates the tables and excludes them from
// introspection, the core refuses an entity that would collide with one. Abstractions is the assembly
// both reference, so the list lives here — internal rather than public, because no consumer outside
// this family has been shown to need it and the shipped seam for a new engine is IAlvoSqlDialect, which
// plugs in *under* the EF adapter and never sees a table name. Public is one word away on the day a
// third-party ISchemaIntrospector needs it; un-publishing a const is a breaking change, so the
// asymmetry decides it.
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.EntityFrameworkCore")]

// IAlvoDataReachability and AlvoReachability (#133) are internal for the reason the port's own remarks give:
// the shared EF path implements the probe once, so no driver and no host has been shown to need the type.
// The assemblies that DO need it are all in this family — the core consumes the port from its readiness
// check, the EF adapter implements it, MMLib.Alvo.Testing holds the contract suite both engines inherit
// (IsPackable=false, so no surface ships), and two test projects name it directly: one stubs it to reach the
// states no real store produces on demand, the other pins that a host-supplied probe beats the driver's
// TryAdd default, and one pins RelationalReachability's own failure classification over a scripted
// DbConnection. The first two grants are already above.
[assembly: InternalsVisibleTo("MMLib.Alvo.Testing")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.EntityFrameworkCore.Tests")]
