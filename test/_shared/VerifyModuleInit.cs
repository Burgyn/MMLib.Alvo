using System.Runtime.CompilerServices;
using VerifyTests;
using VerifyXunit;

namespace MMLib.Alvo.Tests;

// Keep each Verify baseline next to its own test project. The shared file lives
// in test/_shared, so Verify's default (caller-path) would put baselines there;
// projectDirectory is the consuming test project's directory (injected per build),
// which is exactly where the baseline belongs.
internal static class VerifyModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Verifier.DerivePathInfo((_, projectDirectory, type, method) =>
            new PathInfo(projectDirectory, type.Name, method.Name));
}
