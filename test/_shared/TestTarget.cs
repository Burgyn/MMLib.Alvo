using System.Reflection;

namespace MMLib.Alvo.Tests;

internal static class TestTarget
{
    private const string TestsSuffix = ".Tests";

    internal static Assembly Resolve()
    {
        var testAssembly = Assembly.GetExecutingAssembly();
        var explicitName = testAssembly.GetCustomAttribute<ArchTargetAttribute>()?.TargetAssemblyName;
        var targetName = explicitName ?? StripTestsSuffix(testAssembly.GetName().Name!);
        try
        {
            return Assembly.Load(targetName);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Shared tests could not load target assembly '{targetName}' (inferred from test assembly "
                + $"'{testAssembly.GetName().Name}'). Reference the production project it tests, point at it "
                + "with [assembly: ArchTarget(\"...\")], or opt out via <AlvoSharedArchTests>false</AlvoSharedArchTests>.",
                exception);
        }
    }

    private static string StripTestsSuffix(string assemblyName) =>
        assemblyName.EndsWith(TestsSuffix, StringComparison.Ordinal)
            ? assemblyName[..^TestsSuffix.Length]
            : assemblyName;
}
