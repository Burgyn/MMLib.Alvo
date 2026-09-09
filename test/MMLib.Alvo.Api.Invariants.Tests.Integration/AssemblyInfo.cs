// Deliberately no Parallelization attribute, unlike MMLib.Alvo.Api.Tests.Integration next door.
//
// That project serializes its classes because they are container-bound and one of them measures a latency
// budget. Nothing here is either: every world is SQLite in a temp file, and the only shared resource is the
// pinned Vacuum binary, which is resolved once per process behind a Lazy and then only ever executed. So the
// twenty-odd hosts this suite boots are worth booting concurrently — it is the difference between a suite
// that runs before a PR and one nobody waits for.
