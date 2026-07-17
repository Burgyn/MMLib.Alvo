using System.Runtime.CompilerServices;
using VerifyTests;
using VerifyXunit;

namespace MMLib.Alvo.Tests;

internal static class VerifyModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Verifier.DerivePathInfo((_, _, type, method) =>
            new PathInfo(
                directory: Path.Combine(RepositoryRoot.Find(), "test", "_shared"),
                typeName: type.Name,
                methodName: method.Name));
}
