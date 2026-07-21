using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Schema.Tests;

internal static class VerifyModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
