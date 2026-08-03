using System.Runtime.CompilerServices;

// The host is an entry point, not a package: it ships as the mmlib/alvo image and nothing references it, so
// its only consumer is its own suite. That is why the exit contract and the refusal wording stay internal —
// making them public would publish a surface no consumer can reach — and why the suite is granted access to
// them rather than the facts being routed through HTTP, which cannot observe a process's exit code at all.
// The same forgeability caveat as every other InternalsVisibleTo in the family applies: the assemblies are
// unsigned, so this grants access by name alone.
[assembly: InternalsVisibleTo("MMLib.Alvo.Host.Tests")]
