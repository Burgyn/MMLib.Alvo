
// One browser and one host at a time.
//
// Each scenario class brings up its own Alvo — a port, a SQLite file, a Chromium process — because
// the scenarios apply descriptors and write rows, and sharing one world across classes would make
// them order-dependent. Running those worlds *concurrently* is the other half of that trade and it
// is the wrong half: three cold browsers competing for the same machine turn a first render into a
// thirty-second wait, and the suite then fails for load rather than for a defect.
// Spelled the way MMLib.Alvo.Api.Tests.Integration spells it: the CollectionBehavior
// property is obsolete in xunit.v3 and this repository builds warnings as errors.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
