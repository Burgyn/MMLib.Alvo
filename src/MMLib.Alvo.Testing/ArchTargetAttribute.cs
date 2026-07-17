namespace MMLib.Alvo.Testing;

/// <summary>
/// Declares which production assembly the shared architecture rules linked into
/// a test project should run against. Overrides the default convention, which
/// takes the test project's assembly name and removes the <c>.Tests</c> suffix.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ArchTargetAttribute : Attribute
{
    /// <summary>Initializes the attribute with the target assembly's simple name.</summary>
    /// <param name="targetAssemblyName">
    /// Simple name of the production assembly under test (e.g. <c>MMLib.Alvo.Abstractions</c>).
    /// </param>
    public ArchTargetAttribute(string targetAssemblyName) => TargetAssemblyName = targetAssemblyName;

    /// <summary>Simple name of the production assembly the architecture rules run against.</summary>
    public string TargetAssemblyName { get; }
}
