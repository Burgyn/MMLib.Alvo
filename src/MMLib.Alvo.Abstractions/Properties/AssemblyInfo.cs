using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MMLib.Alvo")]

// DestructiveChangeGuard moved here from the core when MigrationResult.EnsureApplied started needing the
// same summary, and its facts stayed in the core's suite beside SchemaMigrationRunnerTests — which is the
// other in-repo caller and the reason the formatter exists at all. One grant, to the one assembly that
// already tests both sides of it; the same forgeability caveat as every other InternalsVisibleTo in the
// family applies (an unsigned grant is a name match, not a proof).
[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]
