using Xunit;

// Test classes here run one at a time, which is unusual for this repository and deliberate for two reasons.
//
// PagingPerformanceTests measures a p95 against a 50 ms budget. Run in parallel with DataApiOnPostgresTests,
// that measurement is taken while thirteen other API facts are migrating their own databases and starting
// their own hosts on the same machine — so the number would describe the test runner's contention as much as
// the read under test. A latency criterion measured under load it does not control is a flake waiting to be
// re-run until green, and "a flake is a finding" only works if the measurement is worth acting on.
//
// It also means one container exists at a time rather than three: each class owns a PostgresApiEngine, and
// three concurrent postgres:16-alpine containers cost more than the parallelism buys — the classes here are
// container-bound, not CPU-bound.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
