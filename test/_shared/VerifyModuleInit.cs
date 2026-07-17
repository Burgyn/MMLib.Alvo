using System.Reflection;
using System.Runtime.CompilerServices;
using VerifyTests;
using VerifyXunit;

namespace MMLib.Alvo.Tests;

// Keep each Verify baseline next to its own test project (not in test/_shared,
// where the linked source physically lives): the shared file is compiled into
// each test assembly, so derive the directory from that assembly's name — the
// repo-relative test project folder — which is stable in local and CI builds.
internal static class VerifyModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Verifier.DerivePathInfo((_, _, type, method) =>
            new PathInfo(
                directory: Path.Combine(RepositoryRoot.Find(), "test", Assembly.GetExecutingAssembly().GetName().Name!),
                typeName: type.Name,
                methodName: method.Name));
}
