#nullable disable

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Delegates compiled with nullable annotations off, so the compiler emits no nullable metadata for them.</summary>
internal static class CelObliviousDelegates
{
    /// <summary>Gets the number of times <see cref="Echo"/> ran.</summary>
    internal static int Calls { get; set; }

    internal static Func<string, string> Parameterised() => Echo;

    internal static string Echo(string value)
    {
        Calls++;
        return value;
    }
}
