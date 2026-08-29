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
